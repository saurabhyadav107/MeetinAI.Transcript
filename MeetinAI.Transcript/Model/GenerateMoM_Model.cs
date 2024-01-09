using Newtonsoft.Json;

namespace MeetinAI.Transcript.Model
{
    public class PromptData
    {
        public long PId { get; set; }
        public string PText { get; set; }
    }

    public class MeetingTranscriptRequest
    {
        public long MeetingId { get; set; }
    }
    public enum ChatMessageRole
    {
        System,
        Assistant,
        User
    }
    public class ChatMessage
    {
        [JsonProperty ("role")]
        public ChatMessageRole Role { get; set; }

        [JsonProperty ("content")]
        public string Content { get; set; }

        public ChatMessage ( ChatMessageRole role, string content )
        {
            Role = role;
            Content = content;
        }
    }

    public class MeetingTranscript
    {
        public string Speaker { get; set; }
        public string Display { get; set; }
    }
}
