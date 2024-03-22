using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System;
using CJM.BBox2DToolkit;
using CJM.DeepLearningImageProcessor;
using CJM.YOLOXUtils;

public class InferenceController : MonoBehaviour
{
    [Header("Components")]
    [SerializeField, Tooltip("Responsible for image preprocessing")]
    private ImageProcessor imageProcessor;
    [SerializeField, Tooltip("Visualizes detected object bounding boxes")]
    private BoundingBox2DVisualizer boundingBoxVisualizer;
    [SerializeField, Tooltip("Renders the input image on a screen")]
    private MeshRenderer screenRenderer;

    [Header("Data Processing")]
    [Tooltip("The target dimensions for the processed image")]
    [SerializeField] private int targetDim = 224;
    [Tooltip("Flag to normalize input images before passing them to the model.")]
    [SerializeField] private bool normalizeInput = false;
    [Tooltip("Flag to flip input images around the X-axis before passing them to the model.")]
    [SerializeField] private bool flipInput = false;
    [SerializeField, Tooltip("Flag to enable/disable async GPU readback for the input image")]
    private bool useAsyncGPUReadback = false;

    [Header("Output Processing")]
    [SerializeField, Tooltip("Minimum confidence score for an object proposal to be considered"), Range(0, 1)]
    private float confidenceThreshold = 0.5f;
    [SerializeField, Tooltip("Threshold for Non-Maximum Suppression (NMS)"), Range(0, 1)]
    private float nmsThreshold = 0.45f;
    [SerializeField, Tooltip("JSON file with bounding box colormaps")]
    private TextAsset colormapFile;

    [Header("ONNX")]
    [Tooltip("The name of the ONNX models folder")]
    public string onnxModelsDir = "Models";

    private BBox2DInfo[] bboxInfoArray; // Array to store bounding box information
    private bool mirrorScreen = false; // Flag to check if the screen is mirrored
    private Vector2Int offset; // Offset used when cropping the input image
    Texture2D inputTextureCPU;  // The model CPU input texture
                                // List to store label and color pairs for each class
    private List<(string, Color)> colormapList = new List<(string, Color)>();
    // List to store grid and stride information for the YOLOX model
    private List<GridCoordinateAndStride> gridCoordsAndStrides = new List<GridCoordinateAndStride>();
    private int proposalLength; // Length of the proposal array for YOLOX output
    float[] outputArray; // Stores the raw model output
                         // File paths for the available ONNX models
    private List<string> modelPaths = new List<string>();
    // Names of the available ONNX models
    private List<string> modelNames = new List<string>();
    // Names of the available ONNX execution providers
    private List<string> onnxExecutionProviders = new List<string>();


    /// <summary>
    /// Prepare a temporary RenderTexture with the given input dimensions.
    /// </summary>
    /// <param name="inputDims">The input dimensions for the RenderTexture</param>
    /// <returns>A temporary RenderTexture with the specified input dimensions</returns>
    private RenderTexture PrepareInputTexture(Vector2Int inputDims)
    {
        return RenderTexture.GetTemporary(inputDims.x, inputDims.y, 0, RenderTextureFormat.ARGBHalf);
    }
    /// <summary>
    /// Process the input image and apply necessary transformations.
    /// </summary>
    /// <param name="inputTexture">The input RenderTexture to process</param>
    /// <param name="imageTexture">The source image texture</param>
    /// <param name="sourceDims">The source image dimensions</param>
    /// <param name="inputDims">The input dimensions for processing</param>
    private void ProcessInputImage(RenderTexture inputTexture, Texture imageTexture, Vector2Int sourceDims, Vector2Int inputDims)
    {
        // Calculate the offset for cropping the input image
        offset = (sourceDims - inputDims) / 2;

        // Create a temporary render texture to store the cropped image
        RenderTexture sourceTexture = RenderTexture.GetTemporary(sourceDims.x, sourceDims.y, 0, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(imageTexture, sourceTexture);

        // Crop and normalize the input image using Compute Shaders
        if (SystemInfo.supportsComputeShaders)
        {
            imageProcessor.CropImageComputeShader(sourceTexture, inputTexture, offset, inputDims);
            if (normalizeInput) imageProcessor.ProcessImageComputeShader(inputTexture, "NormalizeImage");
            if (flipInput) imageProcessor.ProcessImageComputeShader(inputTexture, "FlipXAxis");
        }

        // Release the temporary render texture
        RenderTexture.ReleaseTemporary(sourceTexture);
    }
    /// <summary>
    /// Update the bounding boxes based on the input dimensions and screen dimensions.
    /// </summary>
    /// <param name="inputDims">The input dimensions for processing</param>
    private void UpdateBoundingBoxes(Vector2Int inputDims)
    {
        // Check if the screen is mirrored
        mirrorScreen = screenRenderer.transform.localScale.z == -1;

        // Get the screen dimensions
        Vector2 screenDims = new Vector2(screenRenderer.transform.localScale.x, screenRenderer.transform.localScale.y);

        // Scale and position the bounding boxes based on the input and screen dimensions
        for (int i = 0; i < bboxInfoArray.Length; i++)
        {
            bboxInfoArray[i].bbox = BBox2DUtility.ScaleBoundingBox(bboxInfoArray[i].bbox, inputDims, screenDims, offset, mirrorScreen);
        }
    }
    /// <summary>
    /// Get the file paths for available ONNX models
    /// </summary>
    private void GetONNXModels()
    {
        // Get the paths for each model folder
        foreach (string file in Directory.GetFiles($"{Application.streamingAssetsPath}/{onnxModelsDir}"))
        {
            if (file.EndsWith(".onnx"))
            {
                // Extract the model folder name
                string modelName = file.Split('\\')[1].Split('.')[0];
                Debug.Log($"modelName: {modelName}");
                // Add name to list of model names
                modelNames.Add(modelName);
                modelPaths.Add(file);
            }
        }
    }
    /// <summary>
    /// Get the names of the available ONNX execution providers
    /// </summary>
    private void GetONNXExecutionProviders()
    {
        // Get the number of available ONNX execution providers
        int providerCount = ONNXRuntimePluginImporter.GetProviderCount();
        Debug.Log($"Provider Count: {providerCount}");

        for (int i = 0; i < providerCount; i++)
        {
            string providerName = Marshal.PtrToStringAnsi(ONNXRuntimePluginImporter.GetProviderName(i));
            Debug.Log(providerName);
            providerName = providerName.Replace("ExecutionProvider", "");
            onnxExecutionProviders.Add(providerName);
        }
        onnxExecutionProviders.Reverse();
    }
    /// <summary>
    /// Update the selected ONNX model
    /// </summary>
    public void UpdateONNXModel()
    {
        int[] inputDims = new int[] {
        inputTextureCPU.width,
        inputTextureCPU.height
    };

        // Load the specified ONNX model
        IntPtr resultPtr = ONNXRuntimePluginImporter.LoadModel(
            modelPaths[0],
            onnxExecutionProviders[1],
            inputDims);

        string return_msg = Marshal.PtrToStringAnsi(resultPtr); // Convert IntPtr to string

        Debug.Log($"Return message: {return_msg}");
    }
    private void OnCompleteReadback(AsyncGPUReadbackRequest request, Texture2D targetTexture, RenderTexture sourceTexture)
    {
        if (request.hasError)
        {
            Debug.Log("GPU readback error detected.");
            return;
        }

        // Make sure the Texture2D is not null
        if (targetTexture)
        {
            // Check if dimensions of targetTexture and sourceTexture are different
            if (targetTexture.width != sourceTexture.width || targetTexture.height != sourceTexture.height)
            {
                // Re-create the Texture2D with the same dimensions as the RenderTexture
                targetTexture.Reinitialize(sourceTexture.width, sourceTexture.height);
                // It's usually a good idea to clear out any previous data after resizing
                targetTexture.Apply();
            }

            // Fill Texture2D with raw data from the AsyncGPUReadbackRequest
            targetTexture.LoadRawTextureData(request.GetData<uint>());
            // Apply changes to Texture2D
            targetTexture.Apply();
        }
    }

    /// <summary>
    /// Update the confidence threshold for object detection.
    /// </summary>
    /// <param name="value">The new confidence threshold value</param>
    public void UpdateConfidenceThreshold(float value)
    {
        confidenceThreshold = value;
    }

    // Awake runs when the script instance is being loaded
    private void Awake()
    {
#if !UNITY_EDITOR
    // Define the path for the DirectML.dll file in the StreamingAssets folder
    string sourcePath = $"{Application.dataPath}/Plugins/x86_64/DirectML.dll";

    string dataPath = Application.dataPath;
    string buildDir = Directory.GetParent(dataPath).ToString();

    // Define the destination path for the DirectML.dll file
    string targetPath = $"{buildDir}/DirectML.dll";
    // Only copy the file if it is not already present at the destination
    if (!File.Exists(targetPath)) File.Copy(sourcePath, targetPath);
#endif
    }
    // Start is called before the first frame update
    private void Start()
    {
        colormapList = ColormapUtility.LoadColorMapList(colormapFile); // Load colormap information from JSON file
        proposalLength = colormapList.Count + YOLOXConstants.NumBBoxFields; // Calculate proposal length

        // Get the file paths for available ONNX models
        GetONNXModels();
        // Initialize the ONNX Runtime API
        ONNXRuntimePluginImporter.InitOrtAPI();
        // Get the names of available ONNX execution providers
        GetONNXExecutionProviders();
    }
    /// <summary>
    /// Update the InferenceController every frame, processing the input image and updating the UI and bounding boxes.
    /// </summary>
    private void Update()
    {
        // Get the input image and dimensions
        var imageTexture = screenRenderer.material.mainTexture;
        var imageDims = new Vector2Int(imageTexture.width, imageTexture.height);
        var inputDims = imageProcessor.CalculateInputDims(imageDims, targetDim);

        // Calculate source and input dimensions for model input
        var sourceDims = inputDims;
        inputDims = YOLOXUtility.CropInputDims(inputDims);

        // Prepare and process the input texture
        RenderTexture inputTexture = PrepareInputTexture(inputDims);
        ProcessInputImage(inputTexture, imageTexture, sourceDims, inputDims);

        // Update the plugin with the current input dimensions
        if (!inputTextureCPU || inputTextureCPU.width != inputTexture.width || inputTextureCPU.height != inputTexture.height)
        {
            inputTextureCPU = new Texture2D(inputDims.x, inputDims.y, TextureFormat.RGB24, false);
            gridCoordsAndStrides = YOLOXUtility.GenerateGridCoordinatesWithStrides(YOLOXConstants.Strides, inputTexture.height, inputTexture.width);

            int output_size = gridCoordsAndStrides.Count * proposalLength;
            outputArray = new float[output_size];
            Debug.Log($"Updating output array to {output_size}");
            Debug.Log($"Input Dims: {inputTextureCPU.width}x{inputTextureCPU.height}");

            // Update the selected ONNX model
            UpdateONNXModel();
        }

        // Download the input image data from the GPU to system memory
        if (useAsyncGPUReadback)
        {
            AsyncGPUReadback.Request(inputTexture, 0, TextureFormat.RGB24, (request) =>
            {
                OnCompleteReadback(request, inputTextureCPU, inputTexture);
            });
        }
        else
        {
            // Download pixel data from GPU to CPU
            RenderTexture.active = inputTexture;
            inputTextureCPU.ReadPixels(new Rect(0, 0, inputTexture.width, inputTexture.height), 0, 0);
            inputTextureCPU.Apply();
        }

        // Pass the input image to the model and get the raw output
        ONNXRuntimePluginImporter.PerformInference(inputTextureCPU.GetRawTextureData(), outputArray, outputArray.Length);

        // Free the memory for the input RenderTexture
        RenderTexture.ReleaseTemporary(inputTexture);

        // Generate bounding box proposals from the output array
        List<BBox2D> proposals = YOLOXUtility.GenerateBoundingBoxProposals(outputArray,
                                                                           gridCoordsAndStrides,
                                                                           colormapList.Count,
                                                                           YOLOXConstants.NumBBoxFields,
                                                                           confidenceThreshold);

        // Apply Non-Maximum Suppression (NMS) to the proposals
        List<int> proposalIndices = BBox2DUtility.NMSSortedBoxes(proposals, nmsThreshold);
        // Create an array of BBox2DInfo objects containing the filtered bounding boxes, class labels, and colors
        bboxInfoArray = YOLOXUtility.GetBBox2DInfos(proposals, proposalIndices, colormapList);


        // Update bounding boxes and user interface
        UpdateBoundingBoxes(inputDims);
        boundingBoxVisualizer.UpdateBoundingBoxVisualizations(bboxInfoArray);
    }
    private void OnApplicationQuit()
    {
        ONNXRuntimePluginImporter.FreeResources();
    }
}