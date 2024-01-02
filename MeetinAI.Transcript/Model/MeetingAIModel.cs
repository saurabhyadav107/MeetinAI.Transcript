using System.Text.Json;
using System.Text;

namespace MeetinAI.Transcript.Model
{
    public class AudioRequest
    {
        public string ? AudioUrl { get; set; }
    }
    public class TranscriptionPhrase
    {
        readonly public int id;
        readonly public string text;
        readonly public string itn;
        readonly public string lexical;
        readonly public int speakerNumber;
        readonly public string offset;
        readonly public double offsetInTicks;

        public TranscriptionPhrase ( int id, string text, string itn, string lexical, int speakerNumber, string offset, double offsetInTicks )
        {
            this.id = id;
            this.text = text;
            this.itn = itn;
            this.lexical = lexical;
            this.speakerNumber = speakerNumber;
            this.offset = offset;
            this.offsetInTicks = offsetInTicks;
        }
    }

    public class SentimentAnalysisResult
    {
        readonly public int speakerNumber;
        readonly public double offsetInTicks;
        readonly public JsonElement document;

        public SentimentAnalysisResult ( int speakerNumber, double offsetInTicks, JsonElement document )
        {
            this.speakerNumber = speakerNumber;
            this.offsetInTicks = offsetInTicks;
            this.document = document;
        }
    }

    public class ConversationAnalysisForSimpleOutput
    {
        readonly public (string aspect, string summary) [] summary;
        readonly public (string category, string text) [] [] PIIAnalysis;

        public ConversationAnalysisForSimpleOutput ( (string, string) [] summary, (string, string) [] [] PIIAnalysis )
        {
            this.summary = summary;
            this.PIIAnalysis = PIIAnalysis;
        }
    }

    public class CombinedRedactedContent
    {
        readonly public int channel;
        readonly public StringBuilder lexical;
        readonly public StringBuilder itn;
        readonly public StringBuilder display;

        public CombinedRedactedContent ( int channel )
        {
            this.channel = channel;
            this.lexical = new StringBuilder ();
            this.itn = new StringBuilder ();
            this.display = new StringBuilder ();
        }
    }


}
