﻿using Azure.Communication.Calling.WindowsClient;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media.Core;

using WinRT.Interop;

namespace CallingQuickstart
{
    public partial class MainPage : Page
    {
        private AppWindow m_AppWindow;

        private CallClient callClient;
        private CallTokenRefreshOptions callTokenRefreshOptions = new CallTokenRefreshOptions(false);
#if ACS
        private CallAgent callAgent;
        private CommunicationCall call;
        private const string authToken = "<AUTHENTICATION_TOKEN>";
#endif
        private LocalOutgoingAudioStream micStream;
        private LocalOutgoingVideoStream cameraStream;

        private BackgroundBlurEffect backgroundBlurVideoEffect = new BackgroundBlurEffect();
        private LocalVideoEffectsFeature localVideoEffectsFeature;

        #region Page initialization
        public MainPage()
        {
            this.InitializeComponent();

            CallButton.IsEnabled = true;
            HangupButton.IsEnabled = !CallButton.IsEnabled;
            MuteLocal.IsChecked = MuteLocal.IsEnabled = !CallButton.IsEnabled;

            QuickstartTitle.Text = $"{Package.Current.DisplayName} - Ready";

            var window = (Application.Current as App)?.m_window as MainWindow;
            IntPtr hWnd = WindowNative.GetWindowHandle(window);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            m_AppWindow = AppWindow.GetFromWindowId(wndId);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await InitCallAgentAndDeviceManagerAsync();

            base.OnNavigatedTo(e);
        }
        #endregion

        protected void InitVideoEffectsFeature(LocalOutgoingVideoStream videoStream) {
            localVideoEffectsFeature = videoStream.Features.VideoEffects;
            localVideoEffectsFeature.VideoEffectEnabled += OnVideoEffectEnabled;
            localVideoEffectsFeature.VideoEffectDisabled += OnVideoEffectDisabled;
            localVideoEffectsFeature.VideoEffectError += OnVideoEffectError;
        }

        private StartCallOptions GetStartCallOptions()
        {
            var startCallOptions = new StartCallOptions() {
                OutgoingAudioOptions = new OutgoingAudioOptions() { 
                    IsMuted = true,
                    Stream = micStream,
                    Filters = new OutgoingAudioFilters()
                    {
                        AnalogAutomaticGainControlEnabled = true,
                        AcousticEchoCancellationEnabled = true,
                        NoiseSuppressionMode = NoiseSuppressionMode.High
                    }
                },
                OutgoingVideoOptions = new OutgoingVideoOptions() { Streams = new OutgoingVideoStream[] { cameraStream } }
            };

            return startCallOptions;
        }

        private JoinCallOptions GetJoinCallOptions()
        {
            return new JoinCallOptions() {
                OutgoingAudioOptions = new OutgoingAudioOptions() { IsMuted = true },
                OutgoingVideoOptions = new OutgoingVideoOptions() { Streams = new OutgoingVideoStream[] { cameraStream } }
            };
        }

        #region UI event handlers
#if ACS
        private async void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedCamerea = CameraList.SelectedItem as VideoDeviceDetails;
            if (cameraStream != null)
            {
                await cameraStream.StopPreviewAsync();
            }
            
            cameraStream = new LocalOutgoingVideoStream(selectedCamerea);
            var localUri = await cameraStream.StartPreviewAsync();
            LocalVideo.Source = MediaSource.CreateFromUri(localUri);

            if (call != null)
            {
                await call.StartVideoAsync(cameraStream);
            }
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            var callString = CalleeTextBox.Text.Trim();

            if (!string.IsNullOrEmpty(callString))
            {
                if (callString.StartsWith("8:")) // 1:1 ACS call
                {
                    call = await StartcallAsync(callString);
                }
                else if (callString.All(char.IsDigit)) // rooms call
                { 
                    call = await StartRoomsCallAsync(callString);
                }
                else if (callString.StartsWith("+")) // 1:1 phone call
                {
                    call = await StartPhoneCallAsync(callString, "+12000000000");
                }
                else if (Guid.TryParse(callString, out Guid groupId))// Join group call by group guid
                {
                    call = await JoinGroupCallByIdAsync(groupId);
                }
                else if (Uri.TryCreate(callString, UriKind.Absolute, out Uri teamsMeetinglink)) //Teams meeting link
                {
                    call = await JoinTeamsMeetingByLinkWithAcsAsync(teamsMeetinglink);
                }
            }

            call.RemoteParticipantsUpdated += OnRemoteParticipantsUpdatedAsync;
            call.StateChanged += OnStateChangedAsync;
        }

        private async void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            var call = this.callAgent?.Calls?.FirstOrDefault();
            if (call != null)
            {
                foreach (var localVideoStream in call.OutgoingVideoStreams)
                {
                    await call.StopVideoAsync(localVideoStream);
                }

                try
                {
                    if (cameraStream != null)
                    {
                        await cameraStream.StopPreviewAsync();
                    }

                    await call.HangUpAsync(new HangUpOptions() { ForEveryone = false });
                }
                catch(Exception ex) 
                { 
                    var errorCode = unchecked((int)(0x0000FFFFU & ex.HResult));
                    if (errorCode != 98) // sam_status_failed_to_hangup_for_everyone (98)
                    {
                        throw;
                    }
                }
            }
        }

        private async void MuteLocal_Click(object sender, RoutedEventArgs e)
        {
            var muteCheckbox = sender as CheckBox;

            if (muteCheckbox != null)
            {
                var call = this.callAgent?.Calls?.FirstOrDefault();

                if ((bool)muteCheckbox.IsChecked)
                {
                    await call?.MuteOutgoingAudioAsync();
                }
                else
                {
                    await call?.UnmuteOutgoingAudioAsync();
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AppTitleBar.Background = call.IsOutgoingAudioMuted ? new SolidColorBrush(Colors.PaleVioletRed) : new SolidColorBrush(Colors.SeaGreen);
                });
            }
        }

#endif

        private void BackgroundBlur_Click(object sender, RoutedEventArgs e)
        {
            if (localVideoEffectsFeature.IsEffectSupported(backgroundBlurVideoEffect))
            {
                var backgroundBlurCheckbox = sender as CheckBox;
                if (backgroundBlurCheckbox.IsChecked.Value)
                {
                    localVideoEffectsFeature.EnableEffect(backgroundBlurVideoEffect);
                }
                else
                {
                    localVideoEffectsFeature.DisableEffect(backgroundBlurVideoEffect);
                }
            }
        }
        #endregion

        #region Video Effects Event Handlers
        private void OnVideoEffectError(object sender, VideoEffectErrorEventArgs e) { }

        private void OnVideoEffectDisabled(object sender, VideoEffectDisabledEventArgs e)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                BackgroundBlur.IsChecked = false;
            });
        }

        private void OnVideoEffectEnabled(object sender, VideoEffectEnabledEventArgs e)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                BackgroundBlur.IsChecked = true;
            });
        }

        #endregion

        #region API event handlers

        private async void OnCallsUpdatedAsync(object sender, CallsUpdatedEventArgs args)
        {
            var removedParticipants = new List<RemoteParticipant>();
            var addedParticipants = new List<RemoteParticipant>();

            foreach(var call in args.RemovedCalls)
            {
                removedParticipants.AddRange(call.RemoteParticipants.ToList<RemoteParticipant>());
            }

            foreach (var call in args.AddedCalls)
            {
                addedParticipants.AddRange(call.RemoteParticipants.ToList<RemoteParticipant>());
            }

            await OnParticipantChangedAsync(removedParticipants, addedParticipants);
        }

#if ACS
        private void OnTokenRefreshRequestedAsync(object sender, CallTokenRefreshRequestedEventArgs e)
        {
            e.CallToken = new CallToken(
                authToken,
                DateTimeOffset.Now.AddHours(-8));  // Parse the expiration from the token
        }

        private async void OnIncomingCallAsync(object sender, IncomingCallReceivedEventArgs args)
        {
            var incomingCall = args.IncomingCall;

            var acceptCallOptions = new AcceptCallOptions() { 
                IncomingVideoOptions = new IncomingVideoOptions()
                {
                    StreamKind = VideoStreamKind.RemoteIncoming
                } 
            };

            call = await incomingCall?.AcceptAsync(acceptCallOptions);
            call.StateChanged += OnStateChangedAsync;
            call.RemoteParticipantsUpdated += OnRemoteParticipantsUpdatedAsync;
        }

        private async void OnStateChangedAsync(object sender, PropertyChangedEventArgs args)
        {
            if (call != null)
            {
                var call = sender as CommunicationCall;

                var state = call.State;

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    m_AppWindow.Title = $"{Package.Current.DisplayName} - {state.ToString()}";

                    HangupButton.IsEnabled = state == CallState.Connected || state == CallState.Ringing;
                    CallButton.IsEnabled = !HangupButton.IsEnabled;
                    MuteLocal.IsEnabled = !CallButton.IsEnabled;
                });

                switch (state)
                {
                    case CallState.Connected:
                        {
                            await call.StartAudioAsync(micStream);
                            this.DispatcherQueue.TryEnqueue(() =>
                            {
                                Stats.Text = $"Call id: {Guid.Parse(call.Id).ToString("D")}, Remote caller id: {call.RemoteParticipants.FirstOrDefault()?.Identifier.RawId}";
                            });

                            break;
                        }
                    case CallState.Disconnected:
                        {
                            call.RemoteParticipantsUpdated -= OnRemoteParticipantsUpdatedAsync;
                            call.StateChanged -= OnStateChangedAsync;

                            this.DispatcherQueue.TryEnqueue(() =>
                            {
                                Stats.Text = $"Call ended: {call.CallEndReason.ToString()}";
                            });

                            call.Dispose();

                            break;
                        }
                    default: break;
                }
            }
        }
#endif
        private async void OnRemoteParticipantsUpdatedAsync(object sender, ParticipantsUpdatedEventArgs args)
        {
            await OnParticipantChangedAsync(
                args.RemovedParticipants.ToList<RemoteParticipant>(),
                args.AddedParticipants.ToList<RemoteParticipant>());
        }

        private async Task OnParticipantChangedAsync(IEnumerable<RemoteParticipant> removedParticipants, IEnumerable<RemoteParticipant> addedParticipants)
        {
            foreach (var participant in removedParticipants)
            {
                foreach(var incomingVideoStream in participant.IncomingVideoStreams)
                {
                    var remoteVideoStream = incomingVideoStream as RemoteIncomingVideoStream;
                    if (remoteVideoStream != null)
                    {
                        await remoteVideoStream.StopPreviewAsync();
                    }
                }
                participant.VideoStreamStateChanged -= OnVideoStreamStateChanged;
            }

            foreach (var participant in addedParticipants)
            {
                participant.VideoStreamStateChanged += OnVideoStreamStateChanged;
            }
        }

        private void OnVideoStreamStateChanged(object sender, VideoStreamStateChangedEventArgs e)
        {
            CallVideoStream callVideoStream = e.Stream;

            switch (callVideoStream.Direction)
            {
                case StreamDirection.Incoming:
                    OnIncomingVideoStreamStateChangedAsync(callVideoStream as IncomingVideoStream);
                    break;
            }
        }

        private async void OnIncomingVideoStreamStateChangedAsync(IncomingVideoStream incomingVideoStream)
        {
            switch (incomingVideoStream.State)
            {
                case VideoStreamState.Available:
                    switch (incomingVideoStream.Kind)
                    {
                        case VideoStreamKind.RemoteIncoming:
                            var remoteVideoStream = incomingVideoStream as RemoteIncomingVideoStream;
                            var uri = await remoteVideoStream.StartPreviewAsync();

                            this.DispatcherQueue.TryEnqueue(() =>
                            {
                                RemoteVideo.Source = MediaSource.CreateFromUri(uri);
                            });
                            break;

                        case VideoStreamKind.RawIncoming:
                            break;
                    }
                    break;

                case VideoStreamState.Started:
                    break;

                case VideoStreamState.Stopping:
                case VideoStreamState.Stopped:
                    if (incomingVideoStream.Kind == VideoStreamKind.RemoteIncoming)
                    {
                        var remoteVideoStream = incomingVideoStream as RemoteIncomingVideoStream;
                        await remoteVideoStream.StopPreviewAsync();
                    }
                    break;

                case VideoStreamState.NotAvailable:
                    break;
            }
        }
        #endregion


        #region Helpers
#if ACS
        private async Task InitCallAgentAndDeviceManagerAsync()
        {
            this.callClient = new CallClient(new CallClientOptions() {
                Diagnostics = new CallDiagnosticsOptions() { 
                        AppName = "CallingQuickstart",
                        AppVersion="1.0",
                        Tags = new[] { "Calling", "ACS", "Windows" }
                    }
                });

            // Set up local video stream using the first camera enumerated
            var deviceManager = await this.callClient.GetDeviceManagerAsync();
            var camera = deviceManager?.Cameras?.FirstOrDefault();
            var mic = deviceManager?.Microphones?.FirstOrDefault();
            micStream = new LocalOutgoingAudioStream();

            CameraList.ItemsSource = deviceManager.Cameras.ToList();

            if (camera != null)
            {
                CameraList.SelectedIndex = 0;
            }

            callTokenRefreshOptions.TokenRefreshRequested += OnTokenRefreshRequestedAsync;

            var tokenCredential = new CallTokenCredential(authToken, callTokenRefreshOptions);

            var callAgentOptions = new CallAgentOptions()
            {
                DisplayName = $"{Environment.MachineName}/{Environment.UserName}",
                //https://github.com/lukes/ISO-3166-Countries-with-Regional-Codes/blob/master/all/all.csv
                EmergencyCallOptions = new EmergencyCallOptions() { CountryCode = "840" }
            };

            try
            {
                this.callAgent = await this.callClient.CreateCallAgentAsync(tokenCredential, callAgentOptions);
                //await this.callAgent.RegisterForPushNotificationAsync(await this.RegisterWNS());
                this.callAgent.CallsUpdated += OnCallsUpdatedAsync;
                this.callAgent.IncomingCallReceived += OnIncomingCallAsync;
            }
            catch(Exception ex)
            {
                if (ex.HResult == -2147024809)
                {
                    // E_INVALIDARG
                    // Handle possible invalid token
                }
            }
        }

        private async Task<CommunicationCall> StartcallAsync(string callee)
        {
            var options = GetStartCallOptions();
            var call = await this.callAgent.StartCallAsync(new[] { new UserCallIdentifier(callee) }, options);
            return call;
        }

        private async Task<CommunicationCall> StartPhoneCallAsync(string callee, string alternateCallerId)
        {
            var options = GetStartCallOptions();
            options.AlternateCallerId = new PhoneNumberCallIdentifier(alternateCallerId);

            var call = await this.callAgent.StartCallAsync( new [] { new PhoneNumberCallIdentifier(callee) }, options);
            return call;
        }

        private async Task<CommunicationCall> JoinGroupCallByIdAsync(Guid groupId)
        {
            var joinCallOptions = GetJoinCallOptions();

            var groupCallLocator = new GroupCallLocator(groupId);
            var call = await this.callAgent.JoinAsync(groupCallLocator, joinCallOptions);
            return call;
        }

        private async Task<CommunicationCall> JoinTeamsMeetingByLinkWithAcsAsync(Uri meetingLink)
        {
            var joinCallOptions = GetJoinCallOptions();

            var teamsMeetingLinkLocator = new TeamsMeetingLinkLocator(meetingLink.AbsoluteUri);
            var call = await callAgent.JoinAsync(teamsMeetingLinkLocator, joinCallOptions);
            return call;
        }

        private async Task<CommunicationCall> StartRoomsCallAsync(String roomId) 
        {
            var joinCallOptions = GetJoinCallOptions();

            var roomCallLocator = new RoomCallLocator(roomId);

            call = await callAgent.JoinAsync(roomCallLocator, joinCallOptions);
            return call;

        }

        #region CaptionsCallFeature
        // Demo code for starting Captions Call Feature
        private async Task StartTeamsCaptionsFeature()
        {
            // get captions feature
            var captionsCallFeature = call.Features.Captions;

            try
            {
                // call captions will be typecasted to TeamsCaptions
                var callcaptions = await captionsCallFeature.GetCaptionsAsync();

                if (callcaptions.CaptionsKind == CaptionsKind.TeamsCaptions)
                {
                    var teamsCaptions = callcaptions as TeamsCaptions;

                    // subscribe to captions events
                    teamsCaptions.CaptionsReceived += TeamsCaptions_CaptionsReceived;
                    teamsCaptions.ActiveCaptionLanguageChanged += TeamsCaptions_ActiveCaptionLanguageChanged;
                    teamsCaptions.ActiveSpokenLanguageChanged += TeamsCaptions_ActiveSpokenLanguageChanged;
                    teamsCaptions.CaptionsEnabledChanged += TeamsCaptions_CaptionsEnabledChanged;

                    // set spoken langauge
                    var supportedSpokenLanguages = teamsCaptions.SupportedSpokenLanguages.ToList();
                    var options = new StartCaptionsOptions();
                    options.SpokenLanguage = "en-us";

                    // start captions
                    await teamsCaptions.StartCaptionsAsync(options);

                    // change spoken language
                    await teamsCaptions.SetSpokenLanguageAsync(supportedSpokenLanguages.First());

                    // set caption language
                    var captionLanguages = teamsCaptions.SupportedCaptionLanguages.ToList();
                    await teamsCaptions.SetCaptionLanguageAsync(captionLanguages.First());
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine("Captions failed " + ex.Message);
            }
        }

        private void TeamsCaptions_CaptionsEnabledChanged(object sender, PropertyChangedEventArgs e)
        {
            var enabled = (sender as TeamsCaptions).IsEnabled;
        }

        private void TeamsCaptions_ActiveSpokenLanguageChanged(object sender, PropertyChangedEventArgs e)
        {
            var spokenLanguage = (sender as TeamsCaptions).ActiveSpokenLanguage;
        }

        private void TeamsCaptions_ActiveCaptionLanguageChanged(object sender, PropertyChangedEventArgs e)
        {
            var captionLanguage = (sender as TeamsCaptions).ActiveCaptionLanguage;
        }

        private void TeamsCaptions_CaptionsReceived(object sender, TeamsCaptionsReceivedEventArgs args)
        {
            // on captions data received
            Debug.WriteLine(args.SpokenText + "/n" + args.CaptionText + "/n" + args.CaptionLanguage + "/n" + args.SpokenLanguage + "/n" + args.Speaker.Identifier);
        }
        #endregion
#endif
        #endregion
    }
}
