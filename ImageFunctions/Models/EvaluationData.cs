using Microsoft.Azure.CognitiveServices.ContentModerator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImageFunctions
{
    public class EvaluationData
    {
		// The URL of the evaluated image.
		public string ImageUrl;
		// The image moderation results.
		public Evaluate ImageModeration;
		// The text detection results.
		public OCR TextDetection;
		// The face detection results;
		public FoundFaces FaceDetection;
    }
}
