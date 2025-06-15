using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HuschRagFlowEngineFunctionApp.Models
{
    public class QuestionConfig
    {
        [JsonProperty("QuestionText")]
        public string QuestionText { get; set; }
        [JsonProperty("QuestionTextForEmbedding")]
        public string QuestionTextForEmbedding { get; set; }
        [JsonProperty("SystemMessage")]
        public string SystemMessage { get; set; }

        // PageRange can be something like "1-5" or a single page like "1"
        [JsonProperty("PageRange")]
        public string PageRange { get; set; }

        // Optional chunk size override.
        [JsonProperty("ChunkSize")]
        public int? ChunkSize { get; set; }
        [JsonProperty("questionId")]
        public string QuestionId { get; set; }

        // Optional override for how many chunks to take for context.
        [JsonProperty("topN")]
        public int? TopN { get; set; }

        [JsonProperty("boundingBoxReturn")]
        public string BoundingBoxReturn { get; set; }

        [JsonProperty("lookupValues")]
        public List<string>? LookupValues { get; set; }

        public bool IsLookUp { get; set; }
    }
    // Model representing the MatterTypes JSON structure.
    public class MatterTypeConfig
    {
        [JsonProperty("questions")]
        public List<QuestionConfig> Questions { get; set; }
    }
    public class MatterTypesRequest
    {
        [JsonProperty("MatterTypes")]
        public Dictionary<string, MatterTypeConfig> MatterTypes { get; set; }
    }

}
