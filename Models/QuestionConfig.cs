using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace HuschRagFlowEngineFunctionApp.Models
{
    public class QuestionConfig
    {
        [JsonProperty("QuestionText")]
        [Required]
        public string QuestionText { get; set; } = string.Empty;

        [JsonProperty("QuestionTextForEmbedding")]
        public string? QuestionTextForEmbedding { get; set; }

        [JsonProperty("SystemMessage")]
        public string? SystemMessage { get; set; }

        // PageRange can be something like "1-5" or a single page like "1"
        [JsonProperty("PageRange")]
        public string? PageRange { get; set; }

        // Optional chunk size override.
        [JsonProperty("ChunkSize")]
        [Range(100, 10000, ErrorMessage = "ChunkSize must be between 100 and 10000")]
        public int? ChunkSize { get; set; }

        [JsonProperty("questionId")]
        public string? QuestionId { get; set; }

        // Optional override for how many chunks to take for context.
        [JsonProperty("topN")]
        [Range(1, 20, ErrorMessage = "TopN must be between 1 and 20")]
        public int? TopN { get; set; }

        [JsonProperty("boundingBoxReturn")]
        public string? BoundingBoxReturn { get; set; }

        [JsonProperty("lookupValues")]
        public List<string>? LookupValues { get; set; }

        [JsonProperty("isLookUp")]
        public bool IsLookUp { get; set; }

        /// <summary>
        /// Validates if the page range is in correct format
        /// </summary>
        public bool IsValidPageRange()
        {
            if (string.IsNullOrWhiteSpace(PageRange))
                return true; // Optional field

            // Check for single page number
            if (int.TryParse(PageRange, out int singlePage))
                return singlePage > 0;

            // Check for range format (e.g., "1-5")
            if (PageRange.Contains('-'))
            {
                var parts = PageRange.Split('-');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0].Trim(), out int start) &&
                    int.TryParse(parts[1].Trim(), out int end))
                {
                    return start > 0 && end > 0 && start <= end;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the page numbers from the page range
        /// </summary>
        public List<int> GetPageNumbers()
        {
            var pages = new List<int>();

            if (string.IsNullOrWhiteSpace(PageRange))
                return pages;

            // Single page
            if (int.TryParse(PageRange, out int singlePage))
            {
                pages.Add(singlePage);
                return pages;
            }

            // Range
            if (PageRange.Contains('-'))
            {
                var parts = PageRange.Split('-');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0].Trim(), out int start) &&
                    int.TryParse(parts[1].Trim(), out int end))
                {
                    for (int i = start; i <= end; i++)
                    {
                        pages.Add(i);
                    }
                }
            }

            return pages;
        }

        /// <summary>
        /// Gets the effective question text for embedding (falls back to QuestionText if not specified)
        /// </summary>
        public string GetEffectiveQuestionTextForEmbedding()
        {
            return !string.IsNullOrWhiteSpace(QuestionTextForEmbedding) ? QuestionTextForEmbedding : QuestionText;
        }

        /// <summary>
        /// Gets the effective chunk size (uses default if not specified)
        /// </summary>
        public int GetEffectiveChunkSize(int defaultChunkSize = 4000)
        {
            return ChunkSize ?? defaultChunkSize;
        }

        /// <summary>
        /// Gets the effective top N value (uses default if not specified)
        /// </summary>
        public int GetEffectiveTopN(int defaultTopN = 5)
        {
            return TopN ?? defaultTopN;
        }
    }

    // Model representing the MatterTypes JSON structure.
    public class MatterTypeConfig
    {
        [JsonProperty("questions")]
        [Required]
        public List<QuestionConfig> Questions { get; set; } = new();

        /// <summary>
        /// Validates all questions in this matter type
        /// </summary>
        public bool IsValid()
        {
            return Questions != null &&
                   Questions.Any() &&
                   Questions.All(q => !string.IsNullOrWhiteSpace(q.QuestionText) && q.IsValidPageRange());
        }
    }

    public class MatterTypesRequest
    {
        [JsonProperty("MatterTypes")]
        [Required]
        public Dictionary<string, MatterTypeConfig> MatterTypes { get; set; } = new();

        /// <summary>
        /// Gets all questions from all matter types
        /// </summary>
        public List<QuestionConfig> GetAllQuestions()
        {
            return MatterTypes?.Values
                .Where(mt => mt != null)
                .SelectMany(mt => mt.Questions ?? Enumerable.Empty<QuestionConfig>())
                .Where(q => q != null && !string.IsNullOrWhiteSpace(q.QuestionText))
                .ToList() ?? new List<QuestionConfig>();
        }

        /// <summary>
        /// Validates the entire request
        /// </summary>
        public bool IsValid()
        {
            return MatterTypes != null &&
                   MatterTypes.Any() &&
                   MatterTypes.Values.All(mt => mt?.IsValid() == true);
        }

        /// <summary>
        /// Gets questions for a specific matter type
        /// </summary>
        public List<QuestionConfig> GetQuestionsForMatterType(string matterType)
        {
            if (string.IsNullOrWhiteSpace(matterType) ||
                MatterTypes == null ||
                !MatterTypes.TryGetValue(matterType, out var config))
            {
                return new List<QuestionConfig>();
            }

            return config.Questions?.Where(q => !string.IsNullOrWhiteSpace(q.QuestionText)).ToList()
                   ?? new List<QuestionConfig>();
        }
    }
}