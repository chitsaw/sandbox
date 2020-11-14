using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using Microsoft.Psi;
using Microsoft.Psi.Audio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace Transcription
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ConversationTranscriber conversationTranscriber;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
        }

        public ObservableCollection<string> Speakers { get; } = new ObservableCollection<string>();

        public string Region { get; set; } = "westus";

        public string SubscriptionKey { get; set; } = "<enter subscription key here>";

        private async Task<string> GetVoiceSignatureString()
        {
            var audioStream = new MemoryStream();
            var writer = new WaveDataWriterClass(audioStream, WaveFormat.Create16kHz1Channel16BitPcm());

            using (var p = Pipeline.Create())
            {
                var capture = new AudioCapture(p, WaveFormat.Create16kHz1Channel16BitPcm());
                capture.Do(audio => writer.Write(audio.Data.DeepClone()));
                p.RunAsync();
                await Task.Delay(5000);
                writer.Flush();
            }

            var content = new ByteArrayContent(audioStream.GetBuffer(), 0, (int)audioStream.Length);
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", this.SubscriptionKey);
            var response = await client.PostAsync($"https://signature.{this.Region}.cts.speech.microsoft.com/api/v1/Signature/GenerateVoiceSignatureFromByteArray", content);

            var jsonData = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<VoiceSignature>(jsonData);
            return JsonConvert.SerializeObject(result.Signature);
        }

        public async Task TranscribeConversationsAsync(IEnumerable<string> voiceSignatureStringUsers)
        {
            uint samplesPerSecond = 16000;
            byte bitsPerSample = 16;
            byte channels = 8; // 7 + 1 channels

            var config = SpeechConfig.FromSubscription(this.SubscriptionKey, this.Region);
            config.SetProperty("ConversationTranscriptionInRoomAndOnline", "true");
            var stopRecognition = new TaskCompletionSource<int>();
          
            using (var audioInput = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(samplesPerSecond, bitsPerSample, channels)))
            {
                var meetingID = Guid.NewGuid().ToString();
                using (var conversation = await Conversation.CreateConversationAsync(config, meetingID))
                {
                    // create a conversation transcriber using audio stream input
                    using (this.conversationTranscriber = new ConversationTranscriber(AudioConfig.FromStreamInput(audioInput)))
                    {
                        conversationTranscriber.Transcribing += (s, e) =>
                        {
                            this.SetText($"TRANSCRIBING: Text={e.Result.Text} SpeakerId={e.Result.UserId}");
                        };

                        conversationTranscriber.Transcribed += (s, e) =>
                        {
                            if (e.Result.Reason == ResultReason.RecognizedSpeech)
                            {
                                this.SetText($"TRANSCRIBED: Text={e.Result.Text} SpeakerId={e.Result.UserId}");
                            }
                            else if (e.Result.Reason == ResultReason.NoMatch)
                            {
                                this.SetText($"NOMATCH: Speech could not be recognized.");
                            }
                        };

                        conversationTranscriber.Canceled += (s, e) =>
                        {
                            this.SetText($"CANCELED: Reason={e.Reason}");

                            if (e.Reason == CancellationReason.Error)
                            {
                                this.SetText($"CANCELED: ErrorCode={e.ErrorCode}");
                                this.SetText($"CANCELED: ErrorDetails={e.ErrorDetails}");
                                this.SetText($"CANCELED: Did you update the subscription info?");
                                stopRecognition.TrySetResult(0);
                            }
                        };

                        conversationTranscriber.SessionStarted += (s, e) =>
                        {
                            this.SetText($"\nSession started event. SessionId={e.SessionId}");
                        };

                        conversationTranscriber.SessionStopped += (s, e) =>
                        {
                            this.SetText($"\nSession stopped event. SessionId={e.SessionId}");
                            this.SetText("\nStop recognition.");
                            stopRecognition.TrySetResult(0);
                        };

                        // Add participants to the conversation.
                        int i = 1;
                        foreach (var voiceSignatureStringUser in voiceSignatureStringUsers)
                        {
                            var speaker = Participant.From($"User{i++}", "en-US", voiceSignatureStringUser);
                            await conversation.AddParticipantAsync(speaker);
                        }

                        // Join to the conversation and start transcribing
                        await conversationTranscriber.JoinConversationAsync(conversation);
                        await conversationTranscriber.StartTranscribingAsync().ConfigureAwait(false);

                        using (var p = Pipeline.Create())
                        {
                            var store = PsiStore.Create(p, "Transcribe", @"D:\Temp");
                            var capture = new AudioCapture(p, WaveFormat.CreatePcm((int)samplesPerSecond, bitsPerSample, channels)).Write("Audio", store);
                            capture.Do(audio => audioInput.Write(audio.Data));
                            p.RunAsync();

                            // waits for completion, then stop transcription
                            await stopRecognition.Task;
                        }

                        await conversationTranscriber.StopTranscribingAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private void SetText(string text)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(
                () =>
                {
                    this.Transcription.AppendText($"\r\n{text}");
                    this.Transcription.Select(this.Transcription.Text.Length, 0);
                    this.Transcription.ScrollToEnd();
                }));
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            ((UIElement)sender).IsEnabled = false;
            string speakerSig = await this.GetVoiceSignatureString();
            this.Speakers.Add(speakerSig);
            ((UIElement)sender).IsEnabled = true;
        }

        private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
        {
            ((UIElement)sender).IsEnabled = false;
            await this.TranscribeConversationsAsync(this.Speakers);
            ((UIElement)sender).IsEnabled = true;
        }

        private async void StopTranscribingButton_Click(object sender, RoutedEventArgs e)
        {
            ((UIElement)sender).IsEnabled = false;
            await this.conversationTranscriber.StopTranscribingAsync();
            ((UIElement)sender).IsEnabled = true;
        }
    }

    public static class AudioExtensions
    {
        // Use this as an alternative to inject an extra silent channel into the stream
        public static IProducer<AudioBuffer> AddSilentChannel(this IProducer<AudioBuffer> audioStream)
        {
            byte[] audioWithExtraChannel = null;

            return audioStream.Select(
                audio =>
                {
                    int bytesPerChannel = audio.Length / audio.Format.Channels;
                    int bytesPerSample = audio.Format.BitsPerSample / 8;
                    if (audioWithExtraChannel?.Length != audio.Length + bytesPerChannel)
                    {
                        audioWithExtraChannel = new byte[audio.Length + bytesPerChannel];
                    }

                    for (int i = 0, j = 0; i < audio.Data.Length; )
                    {
                        for (int k = 0; k < audio.Format.Channels; k++)
                        {
                            for (int l = 0; l < bytesPerSample; l++)
                            {
                                audioWithExtraChannel[j++] = audio.Data[i++];
                            }
                        }

                        // inject silent sample
                        for (int l = 0; l < bytesPerSample; l++)
                        {
                            audioWithExtraChannel[j++] = 0;
                        }
                    }

                    var newFormat = WaveFormat.Create16BitPcm((int)audio.Format.SamplesPerSec, audio.Format.Channels + 1);
                    return new AudioBuffer(audioWithExtraChannel, newFormat);
                });
        }
    }
}
