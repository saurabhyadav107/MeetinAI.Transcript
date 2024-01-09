using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Data;
using System.Data.SqlClient;
using System.Xml;
using MeetinAI.Transcript.Model;
using MeetinAI.Transcript.Helper;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Configuration;
using Azure.AI.OpenAI;
using Azure;





namespace MeetinAI.Transcript.Controllers
{

    public class DatabaseHelper
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;


        public DatabaseHelper ( IConfiguration configuration )
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString ("MeetinAIConnection");

        }

        public SqlConnection GetConnection ()
        {
            SqlConnection connection = new SqlConnection (_connectionString);
            connection.Open ();
            return connection;
        }
    }

    [Route ("api/")]
    [ApiController]
    public class TranscriptionController : ControllerBase
    {
        private readonly ILogger<TranscriptionController> _logger;
        private readonly UserConfig _userConfig;
        private const int waitSeconds = 10;

        private readonly DatabaseHelper _databaseHelper;



        // This should not change unless you switch to a new version of the Speech REST API.
        private const string speechTranscriptionPath = "speechtotext/v3.1/transcriptions";

        // These should not change unless you switch to a new version of the Cognitive Language REST API.
        private const string sentimentAnalysisPath = "language/:analyze-text";
        private const string sentimentAnalysisQuery = "api-version=2022-05-01";
        private const string conversationAnalysisPath = "/language/analyze-conversations/jobs";
        private const string conversationAnalysisQuery = "api-version=2022-10-01-preview";
        private const string conversationSummaryModelVersion = "latest";
        private readonly IConfiguration _configuration;
        bool result = false;





        public TranscriptionController ( ILogger<TranscriptionController> logger, IConfiguration configuration )
        {
            _logger = logger;
            _userConfig = new UserConfig (args: new string [] { "--jsonInput", "your_json_input_value" }, // Add other parameters as needed
            usage: "your_usage_value");
            // Other initialization...
            _databaseHelper = new DatabaseHelper (configuration);
        }
       

        [HttpPost ("GenerateMeetingTranscript")]
        public async Task<IActionResult> TranscribeAudio ( [FromBody] AudioRequest audioRequest )
        {
            try
            {

                string getAudioURL = await GetMeetingFiles (audioRequest.MeetinId) ;
                var transcriptionId = await CreateTranscription (getAudioURL);
                await WaitForTranscription (transcriptionId);

                // Retrieve the transcription data
                var transcriptionFiles = await GetTranscriptionFiles (transcriptionId);
                var transcriptionUri = GetTranscriptionUri (transcriptionFiles);
                var transcription = await GetTranscription (transcriptionUri);

                var phrases = transcription
                    .GetProperty ("recognizedPhrases").EnumerateArray ()
                    .OrderBy (phrase => phrase.GetProperty ("offsetInTicks").GetDouble ());
                transcription = AddOrChangeValueInJsonElement<IEnumerable<JsonElement>> (transcription, "recognizedPhrases", phrases);

                // Process the transcription and return the result
                var transcriptionPhrases = GetTranscriptionPhrases (transcription);
                var sentimentAnalysisResults = GetSentimentAnalysis (transcriptionPhrases);
                JsonElement [] sentimentConfidenceScores = GetSentimentConfidenceScores (sentimentAnalysisResults);
                var conversationItems = TranscriptionPhrasesToConversationItems (transcriptionPhrases);
                var conversationAnalysisUrl = await RequestConversationAnalysis (conversationItems);
                await WaitForConversationAnalysis (conversationAnalysisUrl);
                JsonElement conversationAnalysis = await GetConversationAnalysis (conversationAnalysisUrl);
                var Finalresult = PrintSimpleOutput (transcriptionPhrases, sentimentAnalysisResults, conversationAnalysis);
                

                if (_userConfig.outputFilePath is string outputFilePathValue)
                {
                    PrintFullOutput (outputFilePathValue,transcription, sentimentConfidenceScores, transcriptionPhrases, conversationAnalysis);
                    
                    string jsonContent = System.IO.File.ReadAllText (outputFilePathValue);

                    if (System.IO.File.Exists(outputFilePathValue))
                    {
                        System.IO.File.Delete (outputFilePathValue);
                    }


                    JsonDocument jsonDocument = JsonDocument.Parse (jsonContent);
                    JsonElement rootElement = jsonDocument.RootElement;


                    XmlDocument xmlDocument = JsonToXmlConversionMethod (rootElement);
                    string xmlOutput = xmlDocument.OuterXml;
                    long MeetingId = audioRequest.MeetinId;
                    SaveMeetingOutput (xmlOutput, MeetingId);
                    
                }
                var generateMeetingResponsesTask = GenerateMeetingResponses (new MeetingTranscriptRequest { MeetingId = audioRequest.MeetinId });
                await generateMeetingResponsesTask;

                var message = result ? "Data saved successfully" : "Error occurred";
                var status = result ? "200" : "400"; 

                return Ok (new 
                { 
                    message,
                    status
                });

            }
            catch (Exception ex)
            {
                _logger.LogError (ex, "An error occurred during transcription.");

                return BadRequest (new
                {
                    error = "An error occurred during transcription.",
                    details = ex.ToString ()
                });
            }
        
        }

        [HttpPost ("GenerateMeetingResponses")]
        public async Task<IActionResult> GenerateMeetingResponses ( [FromBody] MeetingTranscriptRequest meetingTranscriptRequest )
        {
            try
            {
                List<PromptData> prompts = new ();
                List<MeetingTranscript> getTranscript = new ();
                (getTranscript, long mmtid) = await GetMeetingTranscript (meetingTranscriptRequest.MeetingId);
                var jsondata = JsonSerializer.Serialize (getTranscript);
                prompts = await GetPrompts ();
                foreach (var prompt in prompts)
                {
                    var getPromptResponses = await GeneratePromptResponses (jsondata, prompt.PText);

                    SavePromptResponses (mmtid, getPromptResponses, prompt.PId);
                }
                var message = result ? "Data saved successfully" : "Error occurred";
                var status = result ? "200" : "400";


                return Ok (new
                {
                    message,
                    status
                });
            }
            catch (Exception ex)
            {
                return BadRequest (new { error = $"An error occurred: {ex.Message}" });
            }
        }


        private async Task<string> GetMeetingFiles ( long MeetingId )
        {
            using (SqlConnection connection = _databaseHelper.GetConnection ())
            {               

                using (SqlCommand command = new SqlCommand ("proc_GetMeetingFiles", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.AddWithValue ("@MID", MeetingId);

                    var fileName = await command.ExecuteScalarAsync ();

                    return fileName?.ToString () ?? string.Empty;
                }
            }
        }
        private async Task<(List<MeetingTranscript>, long)> GetMeetingTranscript ( long meetingId )
        {
            try
            {
                long mmtid = 0;
                using (SqlConnection connection = _databaseHelper.GetConnection ())
                {
                    using (SqlCommand command = new SqlCommand ("Proc_GetMeetingTranscript", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue ("@MeetingId", meetingId);
                        using (SqlDataReader reader = command.ExecuteReader ())
                        {
                            List<MeetingTranscript> transcripts = new List<MeetingTranscript> ();

                            while (reader.Read ())
                            {
                                mmtid = reader.GetInt64 (reader.GetOrdinal ("MMTId"));
                                MeetingTranscript transcript = new MeetingTranscript
                                {
                                    Speaker = reader.GetString (reader.GetOrdinal ("Speaker")),
                                    Display = reader.GetString (reader.GetOrdinal ("Display"))
                                };

                                transcripts.Add (transcript);
                            }
                            //string json = JsonConvert.SerializeObject (transcripts, Formatting.Indented);
                            return (transcripts, mmtid);
                        }
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private async Task<List<PromptData>> GetPrompts ()
        {
            try
            {
                using (SqlConnection connection = _databaseHelper.GetConnection ())
                {
                    using (SqlCommand command = new SqlCommand ("Proc_GetPrompts", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        List<PromptData> prompts = new List<PromptData> ();

                        using (SqlDataReader reader = command.ExecuteReader ())
                        {
                            while (reader.Read ())
                            {
                                PromptData promptData = new PromptData
                                {
                                    PId = reader.GetInt64 (0),
                                    PText = reader.GetString (1)
                                };
                                prompts.Add (promptData);
                            }
                        }
                        return prompts;
                    }
                }
            }
            catch (Exception)
            {
                // Handle the exception or rethrow it as needed
                throw;
            }
        }


        private async Task<string> GeneratePromptResponses ( string transcript, string promptText )
        {
            try
            {
                OpenAIClient client = new OpenAIClient (
            new Uri ("https://chipsoftopanai.openai.azure.com/"),
            new AzureKeyCredential ("fcc4301809c043cc94bda7fe5babfa86")
        );
                var options = new ChatCompletionsOptions
                {
                    Messages ={
            new ChatRequestSystemMessage(transcript),
            new ChatRequestUserMessage(promptText),

            },
                    DeploymentName = "gpt-35-turbo",
                    Temperature = (float) 0.7,
                    MaxTokens = 800,
                    NucleusSamplingFactor = (float) 0.95,
                    FrequencyPenalty = 0,
                    PresencePenalty = 0,

                };

                var response = await client.GetChatCompletionsAsync (options);

                var generatedText = response?.Value?.Choices?.FirstOrDefault ()?.Message?.Content;

                return generatedText ?? "No response from OpenAI.";
            }
            catch (Exception ex)
            {
                throw new Exception ($"An error occurred while generating prompt responses: {ex.Message}");
            }
        }


        private void SavePromptResponses ( long mmtid, string getPromptResponses, long promptid )
        {
            try
            {

                using (SqlConnection connection = _databaseHelper.GetConnection ())
                {

                    using (SqlCommand cmd = new SqlCommand ("Proc_SavePromptResponse", connection))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue ("@MMTId", mmtid);
                        cmd.Parameters.AddWithValue ("@PId", promptid);
                        cmd.Parameters.AddWithValue ("@PResponse", getPromptResponses);
                        //SqlDataAdapter adapter = new SqlDataAdapter ();
                        int rowAffected = cmd.ExecuteNonQuery ();
                        connection.Close ();
                        //bool result = false;
                        if (rowAffected > 0)
                        {
                            result = true;

                        }
                        else
                        {
                            result = false;
                        }
                    }
                }

            }
            catch (Exception ex)
            {

            }

        }


        private XmlDocument JsonToXmlConversionMethod ( JsonElement jsonElement )
        {
            XmlDocument xmlDocument = new XmlDocument ();
            XmlElement rootElement = xmlDocument.CreateElement ("Root");
            xmlDocument.AppendChild (rootElement);

            AddJsonToXmlElement (xmlDocument, rootElement, jsonElement);

            return xmlDocument;
        }

        private void AddJsonToXmlElement ( XmlDocument xmlDocument, XmlElement parentNode, JsonElement jsonElement )
        {
            switch (jsonElement.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in jsonElement.EnumerateObject ())
                    {
                        XmlElement propertyElement = xmlDocument.CreateElement (property.Name);
                        parentNode.AppendChild (propertyElement);
                        AddJsonToXmlElement (xmlDocument, propertyElement, property.Value);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var arrayItem in jsonElement.EnumerateArray ())
                    {
                        XmlElement itemElement = xmlDocument.CreateElement ("Item");
                        parentNode.AppendChild (itemElement);
                        AddJsonToXmlElement (xmlDocument, itemElement, arrayItem);
                    }
                    break;

                case JsonValueKind.String:
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    parentNode.InnerText = jsonElement.ToString ();
                    break;

                case JsonValueKind.Null:
                    // Handle null if needed
                    break;
            }
        }


        private void SaveMeetingOutput ( string xmlOutput, long meetingId )
        {
            try
            {
                using (SqlConnection connection = _databaseHelper.GetConnection ())
                {

                    using (SqlCommand cmd = new SqlCommand ("Proc_SaveMettingTranscript", connection))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue ("@MettingId", meetingId);
                        cmd.Parameters.AddWithValue ("@xmlData", xmlOutput);
                        //SqlDataAdapter adapter = new SqlDataAdapter ();
                        int rowAffected = cmd.ExecuteNonQuery ();
                        connection.Close ();
                        //bool result = false;
                        if (rowAffected > 0)
                        {
                            result = true;

                        }
                        else
                        {
                            result = false;
                        }
                    }
                }

            }
            catch (Exception ex)
            {

            }

        }

        private T ObjectFromJsonElement<T> ( JsonElement element )
        {
            var json = element.GetRawText ();
            return JsonSerializer.Deserialize<T> (json);
        }

        private JsonElement AddOrChangeValueInJsonElement<T> ( JsonElement element, string key, T newValue )
        {
            // Convert JsonElement to dictionary.
            Dictionary<string, JsonElement> dictionary = ObjectFromJsonElement<Dictionary<string, JsonElement>> (element);
            // Convert newValue to JsonElement.
            using (JsonDocument doc = JsonDocument.Parse (JsonSerializer.Serialize (newValue)))
            {
                // Add/update key in dictionary to newValue.
                // NOTE: Always clone the root element of the JsonDocument. Otherwise, once it is disposed,
                // when you try to access any data you obtained from it, you get the error:
                // "Cannot access a disposed object.
                // Object name: 'JsonDocument'."
                // See:
                // https://docs.microsoft.com/dotnet/api/system.text.json.jsonelement.clone
                dictionary [key] = doc.RootElement.Clone ();
                // Convert dictionary back to JsonElement.
                using (JsonDocument doc_2 = JsonDocument.Parse (JsonSerializer.Serialize (dictionary)))
                {
                    return doc_2.RootElement.Clone ();
                }
            }
        }

        private async Task<string> CreateTranscription ( string inputAudioURL )
        {


            var uri = new UriBuilder (Uri.UriSchemeHttps, this._userConfig?.speechEndpoint);
            uri.Path = speechTranscriptionPath;

            // Create Transcription API JSON request sample and schema:
            // https://westus.dev.cognitive.microsoft.com/docs/services/speech-to-text-api-v3-0/operations/CreateTranscription
            // Notes:
            // - locale and displayName are required.
            // - diarizationEnabled should only be used with mono audio input.
            var content = new
            {
                contentUrls = new string [] { inputAudioURL },
                properties = new
                {
                    diarizationEnabled = !this._userConfig.useStereoAudio,
                    timeToLive = "PT30M"
                },
                locale = this._userConfig.locale,
                displayName = $"call_center_{DateTime.Now.ToString ()}"
            };
            var response = await RestHelper.SendPost (uri.Uri.ToString (), JsonSerializer.Serialize (content), this._userConfig.speechSubscriptionKey!, new HttpStatusCode [] { HttpStatusCode.Created });
            using (JsonDocument document = JsonDocument.Parse (response.content))
            {
                // Create Transcription API JSON response sample and schema:
                // https://westus.dev.cognitive.microsoft.com/docs/services/speech-to-text-api-v3-0/operations/CreateTranscription
                // NOTE: Remember to call ToString(), EnumerateArray(), GetInt32(), etc. on the return value from GetProperty
                // to convert it from a JsonElement.
                var transcriptionUri = document.RootElement.Clone ().GetProperty ("self").ToString ();
                // The transcription ID is at the end of the transcription URI.
                var transcriptionId = transcriptionUri.Split ("/").Last ();
                // Verify the transcription ID is a valid GUID.
                Guid guid;
                if (!Guid.TryParse (transcriptionId, out guid))
                {
                    throw new Exception ($"Unable to parse response from Create Transcription API:{Environment.NewLine}{response.content}");
                }
                return transcriptionId;
            }
        }

        private async Task<bool> GetTranscriptionStatus ( string transcriptionId )
        {
            var uri = new UriBuilder (Uri.UriSchemeHttps, this._userConfig.speechEndpoint!);
            uri.Path = $"{speechTranscriptionPath}/{transcriptionId}";
            var response = await RestHelper.SendGet (uri.Uri.ToString (), this._userConfig.speechSubscriptionKey!, new HttpStatusCode [] { HttpStatusCode.OK });
            using (JsonDocument document = JsonDocument.Parse (response.content))
            {
                if (0 == string.Compare ("Failed", document.RootElement.Clone ().GetProperty ("status").ToString (), StringComparison.InvariantCultureIgnoreCase))
                {
                    throw new Exception ($"Unable to transcribe audio input. Response:{Environment.NewLine}{response.content}");
                }
                else
                {
                    return 0 == string.Compare ("Succeeded", document.RootElement.Clone ().GetProperty ("status").ToString (), StringComparison.InvariantCultureIgnoreCase);
                }
            }
        }

        private async Task WaitForTranscription ( string transcriptionId )
        {
            var done = false;
            while (!done)
            {
                Console.WriteLine ($"Waiting {waitSeconds} seconds for transcription to complete.");
                Thread.Sleep (waitSeconds * 1000);
                done = await GetTranscriptionStatus (transcriptionId);
            }
            return;
        }

        private async Task<JsonElement> GetTranscriptionFiles ( string transcriptionId )
        {
            var uri = new UriBuilder (Uri.UriSchemeHttps, this._userConfig.speechEndpoint!);
            uri.Path = $"{speechTranscriptionPath}/{transcriptionId}/files";
            var response = await RestHelper.SendGet (uri.Uri.ToString (), this._userConfig.speechSubscriptionKey!, new HttpStatusCode [] { HttpStatusCode.OK });
            using (JsonDocument document = JsonDocument.Parse (response.content))
            {
                return document.RootElement.Clone ();
            }
        }

        private string GetTranscriptionUri ( JsonElement transcriptionFiles )
        {
            return transcriptionFiles.GetProperty ("values").EnumerateArray ()
                .First (value => 0 == String.Compare ("Transcription", value.GetProperty ("kind").ToString (), StringComparison.InvariantCultureIgnoreCase))
                .GetProperty ("links").GetProperty ("contentUrl").ToString ();
        }

        private async Task<JsonElement> GetTranscription ( string transcriptionUri )
        {
            var response = await RestHelper.SendGet (transcriptionUri, "", new HttpStatusCode [] { HttpStatusCode.OK });
            using (JsonDocument document = JsonDocument.Parse (response.content))
            {
                return document.RootElement.Clone ();
            }
        }

        private TranscriptionPhrase [] GetTranscriptionPhrases ( JsonElement transcription )
        {
            return transcription
                .GetProperty ("recognizedPhrases").EnumerateArray ()
                .Select (( phrase, id ) =>
                {
                    var best = phrase.GetProperty ("nBest").EnumerateArray ().First ();
                    // If the user specified stereo audio, and therefore we turned off diarization,
                    // only the channel property is present.
                    // Note: Channels are numbered from 0. Speakers are numbered from 1.
                    int speakerNumber;
                    JsonElement element;
                    if (phrase.TryGetProperty ("speaker", out element))
                    {
                        speakerNumber = element.GetInt32 () - 1;
                    }
                    else if (phrase.TryGetProperty ("channel", out element))
                    {
                        speakerNumber = element.GetInt32 ();
                    }
                    else
                    {
                        throw new Exception ("nBest item contains neither channel nor speaker attribute.");
                    }
                    return new TranscriptionPhrase (id, best.GetProperty ("display").ToString (), best.GetProperty ("itn").ToString (), best.GetProperty ("lexical").ToString (), speakerNumber, phrase.GetProperty ("offset").ToString (), phrase.GetProperty ("offsetInTicks").GetDouble ());
                })
                .ToArray ();
        }

        private async Task DeleteTranscription ( string transcriptionId )
        {
            var uri = new UriBuilder (Uri.UriSchemeHttps, _userConfig.speechEndpoint!);
            uri.Path = $"{speechTranscriptionPath}/{transcriptionId}";
            await RestHelper.SendDelete (uri.Uri.ToString (), _userConfig.speechSubscriptionKey!);
            return;
        }

        private SentimentAnalysisResult [] GetSentimentAnalysis ( TranscriptionPhrase [] phrases )
        {
            var uri = new UriBuilder (Uri.UriSchemeHttps, _userConfig.languageEndpoint);
            uri.Path = sentimentAnalysisPath;
            uri.Query = sentimentAnalysisQuery;

            // Create a map of phrase ID to phrase data so we can retrieve it later.
            var phraseData = new Dictionary<int, (int speakerNumber, double offsetInTicks)> ();

            // Convert each transcription phrase to a "document" as expected by the sentiment analysis REST API.
            var documentsToSend = phrases.Select (phrase =>
            {
                phraseData.Add (phrase.id, (phrase.speakerNumber, phrase.offsetInTicks));
                return new
                {
                    id = phrase.id,
                    language = _userConfig.language,
                    text = phrase.text
                };
            }
            );

            // We can only analyze sentiment for 10 documents per request.
            // We cannot use SelectMany here because the lambda returns a Task.
            var tasks = documentsToSend.Chunk (10).Select (async chunk =>
            {
                var content_1 = new { kind = "SentimentAnalysis", analysisInput = new { documents = chunk } };
                var content_2 = JsonSerializer.Serialize (content_1);
                var response = await RestHelper.SendPost (uri.Uri.ToString (), content_2, _userConfig.languageSubscriptionKey, new HttpStatusCode [] { HttpStatusCode.OK });
                return response.content;
            }
            ).ToArray ();
            Task.WaitAll (tasks);
            return tasks.SelectMany (task =>
            {
                var result = task.Result;
                using (JsonDocument document = JsonDocument.Parse (result))
                {
                    return document.RootElement.Clone ()
                        .GetProperty ("results")
                        .GetProperty ("documents").EnumerateArray ()
                        .Select (document =>
                        {
                            (int speakerNumber, double offsetInTicks) = phraseData [Int32.Parse (document.GetProperty ("id").ToString ())];
                            return new SentimentAnalysisResult (speakerNumber, offsetInTicks, document);
                        }
                        );
                }
            }
            ).ToArray ();
        }

        private string [] GetSentimentsForSimpleOutput ( SentimentAnalysisResult [] sentimentAnalysisResults )
        {
            return sentimentAnalysisResults
                .OrderBy (x => x.offsetInTicks)
                .Select (result => result.document.GetProperty ("sentiment").ToString ())
                .ToArray ();
        }

        private JsonElement [] GetSentimentConfidenceScores ( SentimentAnalysisResult [] sentimentAnalysisResults )
        {
            return sentimentAnalysisResults
                .OrderBy (x => x.offsetInTicks)
                .Select (result => result.document.GetProperty ("confidenceScores"))
                .ToArray ();
        }

        private JsonElement MergeSentimentConfidenceScoresIntoTranscription ( JsonElement transcription, JsonElement [] sentimentConfidenceScores )
        {
            IEnumerable<JsonElement> recognizedPhrases_2 = transcription
                .GetProperty ("recognizedPhrases").EnumerateArray ()
                // Include a counter to retrieve the sentiment data.
                .Select (( phrase, id ) =>
                {
                    IEnumerable<JsonElement> nBest_2 = phrase
                        .GetProperty ("nBest")
                        .EnumerateArray ()
                        // Add the sentiment confidence scores to the JsonElement in the nBest array.
                        // TODO2 We are adding the same sentiment data to each nBest item.
                        // However, the sentiment data are based on the phrase from the first nBest item.
                        // See GetTranscriptionPhrases() and GetSentimentAnalysis().
                        .Select (nBestItem => AddOrChangeValueInJsonElement<JsonElement> (nBestItem, "sentiment", sentimentConfidenceScores [id]));
                    // Update the nBest item in the recognizedPhrases array.
                    return AddOrChangeValueInJsonElement<IEnumerable<JsonElement>> (phrase, "nBest", nBest_2);
                }
                );
            // Update the recognizedPhrases item in the transcription.
            return AddOrChangeValueInJsonElement<IEnumerable<JsonElement>> (transcription, "recognizedPhrases", recognizedPhrases_2);
        }

        private object [] TranscriptionPhrasesToConversationItems ( TranscriptionPhrase [] transcriptionPhrases )
        {
            return transcriptionPhrases.Select (phrase =>
            {
                return new
                {
                    id = phrase.id,
                    text = phrase.text,
                    itn = phrase.itn,
                    lexical = phrase.lexical,
                    // The first person to speak is probably the agent.
                    role = 0 == phrase.speakerNumber ? "Agent" : "Customer",
                    participantId = phrase.speakerNumber
                };
            }
            ).ToArray ();
        }

        private async Task<string> RequestConversationAnalysis ( object [] conversationItems )
        {
            var uri = new UriBuilder (Uri.UriSchemeHttps, this._userConfig.languageEndpoint);
            uri.Path = conversationAnalysisPath;
            uri.Query = conversationAnalysisQuery;

            var displayName = $"call_center_{DateTime.Now.ToString ()}";

            var content = new
            {
                displayName = displayName,
                analysisInput = new
                {
                    conversations = new object []
                    {
                        new
                        {
                            id = "conversation1",
                            language = _userConfig.language,
                            modality = "transcript",
                            conversationItems = conversationItems
                        }
                    }
                },
                tasks = new object []
                {
                    new
                    {
                        taskName = "summary_1",
                        kind = "ConversationalSummarizationTask",
                        parameters = new
                        {
                            modelVersion = conversationSummaryModelVersion,
                            summaryAspects = new string[]
                            {
                                "Issue",
                                "Resolution"
                            }
                        }
                    },
                    new
                    {
                        parameters = new
                        {
                            piiCategories = new[]
                            {
                                "All",
                            },
                            includeAudioRedaction = false,
                            redactionSource = "text",
                            modelVersion = conversationSummaryModelVersion,
                            loggingOptOut = false,
                        },
                        taskName = "PII_1",
                        kind = "ConversationalPIITask"
                    }
                }
            };
            var response = await RestHelper.SendPost (uri.Uri.ToString (), JsonSerializer.Serialize (content), this._userConfig.languageSubscriptionKey, new HttpStatusCode [] { HttpStatusCode.Accepted });
            return response.response.Headers.GetValues ("operation-location").First ();
        }

        private async Task<bool> GetConversationAnalysisStatus ( string conversationAnalysisUrl )
        {
            try
            {
                var response = await RestHelper.SendGet (conversationAnalysisUrl, this._userConfig.languageSubscriptionKey, new HttpStatusCode [] { HttpStatusCode.OK });
                using (JsonDocument document = JsonDocument.Parse (response.content))
                {
                    if (0 == string.Compare ("Failed", document.RootElement.Clone ().GetProperty ("status").ToString (), StringComparison.InvariantCultureIgnoreCase))
                    {
                        throw new Exception ($"Unable to analyze conversation. Response:{Environment.NewLine}{response.content}");
                    }
                    else
                    {
                        return 0 == string.Compare ("Succeeded", document.RootElement.Clone ().GetProperty ("status").ToString (), StringComparison.InvariantCultureIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {

            }
            return false;
        }

        private async Task WaitForConversationAnalysis ( string conversationAnalysisUrl )
        {
            try
            {
                var done = false;
                while (!done)
                {
                    Console.WriteLine ($"Waiting {waitSeconds} seconds for conversation analysis to complete.");
                    Thread.Sleep (waitSeconds * 1000);
                    done = await GetConversationAnalysisStatus (conversationAnalysisUrl);
                }
            }
            catch (Exception ex)
            {


            }
            return;
        }

        private async Task<JsonElement> GetConversationAnalysis ( string conversationAnalysisUrl )
        {
            var response = await RestHelper.SendGet (conversationAnalysisUrl, this._userConfig.languageSubscriptionKey, new HttpStatusCode [] { HttpStatusCode.OK });
            using (JsonDocument document = JsonDocument.Parse (response.content))
            {
                return document.RootElement.Clone ();
            }
        }

        private ConversationAnalysisForSimpleOutput GetConversationAnalysisForSimpleOutput ( JsonElement conversationAnalysis )
        {
            var tasks = conversationAnalysis.GetProperty ("tasks").GetProperty ("items").EnumerateArray ();

            var summaryTask = tasks.First (task => 0 == String.Compare (task.GetProperty ("taskName").ToString (), "summary_1", StringComparison.InvariantCultureIgnoreCase));
            var conversationSummary = summaryTask.GetProperty ("results").GetProperty ("conversations").EnumerateArray ()
                .First ().GetProperty ("summaries").EnumerateArray ()
                .Select (summary => (summary.GetProperty ("aspect").ToString (), summary.GetProperty ("text").ToString ()))
                .ToArray ();

            var PIITask = tasks.First (task => 0 == String.Compare (task.GetProperty ("taskName").ToString (), "PII_1", StringComparison.InvariantCultureIgnoreCase));
            var conversationPIIAnalysis = PIITask.GetProperty ("results").GetProperty ("conversations").EnumerateArray ()
                .First ().GetProperty ("conversationItems").EnumerateArray ()
                .Select (document =>
                {
                    return document.GetProperty ("entities").EnumerateArray ()
                        .Select (entity => (entity.GetProperty ("category").ToString (), entity.GetProperty ("text").ToString ()))
                        .ToArray ();
                })
                .ToArray ();

            return new ConversationAnalysisForSimpleOutput (conversationSummary, conversationPIIAnalysis);
        }

        private string GetSimpleOutput (
            TranscriptionPhrase [] transcriptionPhrases,
            string [] transcriptionSentiments,
            ConversationAnalysisForSimpleOutput conversationAnalysis )
        {
            var result = new StringBuilder ();
            for (var index = 0; index < transcriptionPhrases.Length; index++)
            {
                result.AppendLine ($"Phrase: {transcriptionPhrases [index].text}");
                result.AppendLine ($"Speaker: {transcriptionPhrases [index].speakerNumber}");
                if (index < transcriptionSentiments.Length)
                {
                    result.AppendLine ($"Sentiment: {transcriptionSentiments [index]}");
                }
                if (index < conversationAnalysis.PIIAnalysis.Length)
                {
                    if (conversationAnalysis.PIIAnalysis [index].Length > 0)
                    {
                        var entities = conversationAnalysis.PIIAnalysis [index].Aggregate ("",
                            ( result, entity ) => $"{result}    Category: {entity.category}. Text: {entity.text}.{Environment.NewLine}");
                        result.Append ($"Recognized entities (PII):{Environment.NewLine}{entities}");
                    }
                    else
                    {
                        result.AppendLine ($"Recognized entities (PII): none.");
                    }
                }
                result.AppendLine ();
            }
            result.AppendLine (conversationAnalysis.summary.Aggregate ($"Conversation summary:{Environment.NewLine}",
                ( result, item ) => $"{result}    {item.aspect}: {item.summary}.{Environment.NewLine}"));
            return result.ToString ();
        }

        private string PrintSimpleOutput ( TranscriptionPhrase [] transcriptionPhrases, SentimentAnalysisResult [] sentimentAnalysisResults, JsonElement conversationAnalysis )
        {
            string [] sentiments = GetSentimentsForSimpleOutput (sentimentAnalysisResults);
            ConversationAnalysisForSimpleOutput conversation = GetConversationAnalysisForSimpleOutput (conversationAnalysis);
            return (GetSimpleOutput (transcriptionPhrases, sentiments, conversation));


        }


        private object GetConversationAnalysisForFullOutput ( TranscriptionPhrase [] transcriptionPhrases, JsonElement conversationAnalysis )
        {
            // Get the conversation summary and conversation PII analysis task results.
            IEnumerable<JsonElement> tasks = conversationAnalysis.GetProperty ("tasks").GetProperty ("items").EnumerateArray ();
            JsonElement conversationSummaryResults = tasks.First (task => 0 == String.Compare (task.GetProperty ("taskName").ToString (), "summary_1", StringComparison.InvariantCultureIgnoreCase)).GetProperty ("results");
            JsonElement conversationPiiResults = tasks.First (task => 0 == String.Compare (task.GetProperty ("taskName").ToString (), "PII_1", StringComparison.InvariantCultureIgnoreCase)).GetProperty ("results");
            // There should be only one conversation.
            JsonElement conversation = conversationPiiResults.GetProperty ("conversations").EnumerateArray ().First ();
            // Order conversation items by ID so they match the order of the transcription phrases.
            IEnumerable<JsonElement> conversationItems = conversation.GetProperty ("conversationItems").EnumerateArray ().OrderBy (conversationItem => Int32.Parse (conversationItem.GetProperty ("id").ToString ()));
            var combinedRedactedContent = new CombinedRedactedContent [] { new CombinedRedactedContent (0), new CombinedRedactedContent (1) };
            IEnumerable<JsonElement> conversationItems_2 = conversationItems.Select (( conversationItem, index ) =>
            {
                // Get the channel and offset for this conversation item from the corresponding transcription phrase.
                var channel = transcriptionPhrases [index].speakerNumber;
                // Add channel and offset to conversation item JsonElement.
                var conversationItem_2 = AddOrChangeValueInJsonElement<int> (conversationItem, "channel", channel);
                var conversationItem_3 = AddOrChangeValueInJsonElement<string> (conversationItem_2, "offset", transcriptionPhrases [index].offset);
                // Get the text, lexical, and itn fields from redacted content, and append them to the combined redacted content for this channel.
                var redactedContent = conversationItem.GetProperty ("redactedContent");
                combinedRedactedContent [channel].display.AppendFormat ("{0} ", redactedContent.GetProperty ("text").ToString ());
                combinedRedactedContent [channel].lexical.AppendFormat ("{0} ", redactedContent.GetProperty ("lexical").ToString ());
                combinedRedactedContent [channel].itn.AppendFormat ("{0} ", redactedContent.GetProperty ("itn").ToString ());
                return conversationItem_3;
            }
            );
            // Update the conversationItems item in the conversation JsonElement.
            JsonElement conversation_2 = AddOrChangeValueInJsonElement<IEnumerable<JsonElement>> (conversation, "conversationItems", conversationItems_2);
            return new
            {
                conversationSummaryResults = conversationSummaryResults,
                conversationPiiResults = new
                {
                    combinedRedactedContent = combinedRedactedContent.Select (item =>
                    {
                        return new
                        {
                            channel = item.channel,
                            display = item.display.ToString (),
                            itn = item.itn.ToString (),
                            lexical = item.lexical.ToString ()
                        };
                    }
                    ),
                    conversations = new JsonElement [] { conversation_2 }
                }
            };
        }

        private void PrintFullOutput (string outputFilePathValue ,JsonElement transcription, JsonElement [] sentimentConfidenceScores, TranscriptionPhrase [] transcriptionPhrases, JsonElement conversationAnalysis )
        {
            var results = new
            {
                transcription = MergeSentimentConfidenceScoresIntoTranscription (transcription, sentimentConfidenceScores),
                conversationAnalyticsResults = GetConversationAnalysisForFullOutput (transcriptionPhrases, conversationAnalysis)
            };

            // Use a different name for the variable
            System.IO.File.WriteAllText( outputFilePathValue,JsonSerializer.Serialize (results, new JsonSerializerOptions () { WriteIndented = true, Encoder = JavaScriptEncoder.Default }));
        }

    }
}
