// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}

// Learn how to locally debug an Event Grid-triggered function:
//    https://aka.ms/AA30pjh

// Use for local testing:
//   https://{ID}.ngrok.io/runtime/webhooks/EventGrid?functionName=Thumbnail

using Microsoft.Azure.CognitiveServices.ContentModerator;
using Microsoft.Azure.CognitiveServices.ContentModerator.Models;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFunctions {
	public static class Thunbnail {
		private static string CMRegion = Environment.GetEnvironmentVariable("CM_REGION");
		private static string CMSubscriptionKey = Environment.GetEnvironmentVariable("CM_SUBSCRIPTION_KEY");
		private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
		private static readonly string CMBaseUrl = $"https://{CMRegion}.api.cognitive.microsoft.com";
		private static string OutputFile = "ModerationOutput.json"; // TODO - remove this later, after testing.

		public static ContentModeratorClient NewClient() {
			ContentModeratorClient client = new ContentModeratorClient(new ApiKeyServiceClientCredentials(CMSubscriptionKey));
			client.Endpoint = CMBaseUrl;
			return client;
		}

		private static string GetBlobNameFromUrl(string bloblUrl) {
			var uri = new Uri(bloblUrl);
			var cloudBlob = new CloudBlob(uri);
			return cloudBlob.Name;
		}

		private static IImageEncoder GetEncoder(string extension) {
			IImageEncoder encoder = null;

			extension = extension.Replace(".", "");

			var isSupported = Regex.IsMatch(extension, "gif|png|jpe?g", RegexOptions.IgnoreCase);

			if (isSupported) {
				switch (extension) {
					case "png":
						encoder = new PngEncoder();
						break;
					case "jpg":
						encoder = new JpegEncoder();
						break;
					case "jpeg":
						encoder = new JpegEncoder();
						break;
					case "gif":
						encoder = new GifEncoder();
						break;
					default:
						break;
				}
			}

			return encoder;
		}

		/// <summary>
		/// Send an image url to cognitive services to be checked.
		/// </summary>
		/// <param name="client"></param>
		/// <param name="imageUrl"></param>
		/// <returns></returns>
		private static EvaluationData EvaluateImage(ContentModeratorClient client, string imageUrl) {
			var url = new BodyModel("URL", imageUrl.Trim());
			var imageData = new EvaluationData();
			imageData.ImageUrl = url.Value;

			// Evaluate for adult and racy content.
			imageData.ImageModeration = client.ImageModeration.EvaluateUrlInput("application/json", url, true);
			Thread.Sleep(1000);

			// Detect and extract text.
			imageData.TextDetection = client.ImageModeration.OCRUrlInput("eng", "application/json", url, true);
			Thread.Sleep(1000);

			// Detect faces.
			imageData.FaceDetection = client.ImageModeration.FindFacesUrlInput("application/json", url, true);
			Thread.Sleep(1000);

			return imageData;
		}

		[FunctionName("Thumbnail")]
		public static async Task Run(
			[EventGridTrigger]EventGridEvent eventGridEvent,
			[Blob("{data.url}", FileAccess.Read)] Stream input,
			ILogger log) {
			try {
				if (input != null) {
					var createdEvent = ((JObject)eventGridEvent.Data).ToObject<StorageBlobCreatedEventData>();
					var extension = Path.GetExtension(createdEvent.Url);
					var encoder = GetEncoder(extension);

					if (encoder != null) {
						var thumbnailWidth = Convert.ToInt32(Environment.GetEnvironmentVariable("THUMBNAIL_WIDTH"));
						var thumbContainerName = Environment.GetEnvironmentVariable("THUMBNAIL_CONTAINER_NAME");
						var checkedImageContainerName = Environment.GetEnvironmentVariable("CHECKED_IMAGES_CONTAINER_NAME");
						var storageAccount = CloudStorageAccount.Parse(BLOB_STORAGE_CONNECTION_STRING);
						var blobClient = storageAccount.CreateCloudBlobClient();
						var container = blobClient.GetContainerReference(thumbContainerName);
						var blobName = GetBlobNameFromUrl(createdEvent.Url);
						var blockBlob = container.GetBlockBlobReference(blobName);
						bool isContentApproved = false;
						List<EvaluationData> evaluationData = new List<EvaluationData>();

						// check image by sending to MS Cognitive services content moderator AI
						// first, create new ContentModerator
						using (var client = NewClient()) {
							EvaluationData imageData = EvaluateImage(client, createdEvent.Url);
							evaluationData.Add(imageData);
						};

						using (StreamWriter outputWriter = new StreamWriter(OutputFile, false)) {
							outputWriter.WriteLine(JsonConvert.SerializeObject(evaluationData, Formatting.Indented));
							outputWriter.Flush();
							outputWriter.Close();
						}

						// shrink image once approved
						if (isContentApproved) {
							using (var output = new MemoryStream())
							using (Image<Rgba32> image = SixLabors.ImageSharp.Image.Load(input)) {
								var divisor = image.Width / thumbnailWidth;
								var height = Convert.ToInt32(Math.Round((decimal)(image.Height / divisor)));

								image.Mutate(x => x.Resize(thumbnailWidth, height));
								image.Save(output, encoder);
								output.Position = 0;
								await blockBlob.UploadFromStreamAsync(output);
							}
						}
					}
					else {
						log.LogInformation($"No encoder support for: {createdEvent.Url}");
					}
				}
			}
			catch (Exception ex) {
				log.LogInformation(ex.Message);
				throw;
			}
		}
	}
}
