﻿namespace MeetinAI.Transcript
{
    public class UserConfig
    {

        private const string partialSpeechEndpoint = ".api.cognitive.microsoft.com";
        /// True to treat input audio as stereo; otherwise, treat it as mono.
        readonly public bool useStereoAudio = false;
        /// Language for sentiment analysis and conversation analysis.
        readonly public string language;
        /// Locale for batch transcription.
        readonly public string locale;
        /// Input audio file URL.
        readonly public string? inputAudioURL;
        /// Input batch transcription output file.
        readonly public string? inputFilePath;
        /// Output file path.
        readonly public string? outputFilePath;
        /// The subscription key for your Speech service subscription.
        readonly public string? speechSubscriptionKey;
        /// The endpoint for your Speech service subscription.
        readonly public string? speechEndpoint;
        /// The subscription key for your Cognitive Language subscription.
        readonly public string languageSubscriptionKey;
        /// The endpoint for your Cognitive Language subscription.
        readonly public string languageEndpoint;

        public static string? GetCmdOption ( string [] args, string option )
        {
            int index = Array.FindIndex (args, x => x.Equals (option, StringComparison.OrdinalIgnoreCase));
            if (index > -1 && index < args.Length - 1)
            {
                // We found the option (for example, "--output"), so advance from that to the value (for example, "filename").
                return args [index + 1];
            }
            else
            {
                return null;
            }
        }

        public static bool CmdOptionExists ( string [] args, string option )
        {
            return args.Contains (option);
        }

        public UserConfig ( string [] args, string usage )
        {
            //string? inputAudioURL = "https://github.com/Azure-Samples/cognitive-services-speech-sdk/raw/master/scenarios/call-center/sampledata/Call6_mono_16k_az_apply_loan.wav";
            string? inputFilePath = GetCmdOption (args, "--jsonInput");
            if (inputAudioURL is null && inputFilePath is null)
            {
                throw new ArgumentException ($"Please specify either --input or --jsonInput.{Environment.NewLine}Usage: {usage}");
            }

            string? speechSubscriptionKey = "861a2fdea6af42dea5551fb0d819b89c";
            if (speechSubscriptionKey is null && inputFilePath is null)
            {
                throw new ArgumentException ($"Missing Speech subscription key. Speech subscription key is required unless --jsonInput is present.{Environment.NewLine}Usage: {usage}");
            }
            string? speechEndpoint = null;
            string? speechRegion = "eastus";
            if (speechRegion is string speechRegionValue)
            {
                speechEndpoint = $"{speechRegionValue}{partialSpeechEndpoint}";
            }
            else if (inputFilePath is null)
            {
                throw new ArgumentException ($"Missing Speech region. Speech region is required unless --jsonInput is present.{Environment.NewLine}Usage: {usage}");
            }

            string? languageSubscriptionKey = "35290a3df6c54d23b1f4962b08b251df";
            if (languageSubscriptionKey is null)
            {
                throw new ArgumentException ($"Missing Language subscription key.{Environment.NewLine}Usage: {usage}");
            }
            string? languageEndpoint = "https://eastus.api.cognitive.microsoft.com/";
            if (languageEndpoint is null)
            {
                throw new ArgumentException ($"Missing Language endpoint.{Environment.NewLine}Usage: {usage}");
            }
            languageEndpoint = languageEndpoint.Replace ("https://", "");

            string? language = GetCmdOption (args, "--language");
            if (language is null)
            {
                language = "en";
            }
            string? locale = GetCmdOption (args, "--locale");
            if (locale is null)
            {
                locale = "en-US";
            }

            this.useStereoAudio = CmdOptionExists (args, "--stereo");
            this.language = language;
            this.locale = locale;
            this.inputFilePath = inputFilePath;
            this.outputFilePath = "summary.json";
            this.speechSubscriptionKey = speechSubscriptionKey;
            this.speechEndpoint = speechEndpoint;
            this.languageSubscriptionKey = languageSubscriptionKey;
            this.languageEndpoint = languageEndpoint;
        }

    }
}
