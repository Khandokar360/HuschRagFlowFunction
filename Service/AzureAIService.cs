using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Syncfusion.Blazor.SmartComponents;

namespace HuschRagFlowEngineFunctionApp.Service
{
    public class AzureAIService
    {
        private readonly OpenAIConfiguration _openAIConfiguration;
        private readonly ILogger<AzureAIService>? _logger;
        private ChatParameters? _chatParametersHistory;

        public AzureAIService(OpenAIConfiguration openAIConfiguration, ILogger<AzureAIService>? logger = null)
        {
            _openAIConfiguration = openAIConfiguration ?? throw new ArgumentNullException(nameof(openAIConfiguration));
            _logger = logger;
        }

        /// <summary>
        /// Gets a text completion from the Azure OpenAI service.
        /// </summary>
        /// <param name="prompt">The user prompt to send to the AI service.</param>
        /// <param name="returnAsJson">Indicates whether the response should be returned in JSON format. Defaults to <c>false</c></param>
        /// <param name="appendPreviousResponse">Indicates whether to append previous responses to the conversation history. Defaults to <c>false</c></param>
        /// <param name="systemRole">Specifies the systemRole that is sent to AI Clients. Defaults to <c>null</c></param>
        /// <returns>The AI-generated completion as a string.</returns>
        public async Task<string> GetCompletionAsync(string prompt, bool returnAsJson = false, bool appendPreviousResponse = false, string? systemRole = null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("Prompt cannot be null or whitespace.", nameof(prompt));
            }

            string systemMessage = GetSystemMessage(returnAsJson, systemRole);

            try
            {
                ChatParameters chatParameters = PrepareChatParameters(appendPreviousResponse, systemMessage, prompt);

                var completion = await _openAIConfiguration.GetChatResponseAsync(chatParameters);
                var completionText = completion?.ToString() ?? string.Empty;

                if (appendPreviousResponse && !string.IsNullOrEmpty(completionText))
                {
                    UpdateChatHistory(completionText);
                }

                return completionText;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "An exception occurred while getting AI completion for prompt: {Prompt}", prompt);
                return string.Empty;
            }
        }

        private static string GetSystemMessage(bool returnAsJson, string? systemRole)
        {
            if (returnAsJson)
            {
                return "You are a helpful assistant that only returns and replies with valid, iterable RFC8259 compliant JSON in your responses unless I ask for any other format. Do not provide introductory words such as 'Here is your result' or '```json', etc. in the response";
            }

            return !string.IsNullOrEmpty(systemRole) ? systemRole : "You are a helpful assistant";
        }

        private ChatParameters PrepareChatParameters(bool appendPreviousResponse, string systemMessage, string prompt)
        {
            if (appendPreviousResponse)
            {
                _chatParametersHistory ??= new ChatParameters
                {
                    Messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, systemMessage)
                    }
                };

                _chatParametersHistory.Messages.Add(new ChatMessage(ChatRole.User, prompt));
                return _chatParametersHistory;
            }

            return new ChatParameters
            {
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemMessage),
                    new(ChatRole.User, prompt)
                }
            };
        }

        private void UpdateChatHistory(string completionText)
        {
            _chatParametersHistory?.Messages?.Add(new ChatMessage(ChatRole.Assistant, completionText));
        }

        /// <summary>
        /// Clears the conversation history
        /// </summary>
        public void ClearHistory()
        {
            _chatParametersHistory = null;
        }

        /// <summary>
        /// Gets the current conversation history count
        /// </summary>
        public int GetHistoryCount()
        {
            return _chatParametersHistory?.Messages?.Count ?? 0;
        }
    }
}