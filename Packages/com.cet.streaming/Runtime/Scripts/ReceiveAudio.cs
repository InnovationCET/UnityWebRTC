using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

/*
  This class receives audio from a remote peer and sends it to the Audio Source
*/

public class ReceiveAudio : MonoBehaviour
{
  [SerializeField] private AudioSource outputAudioSource;
  public string LogTag = "RecvAudio";
  public string localId;
  public string localId2 = "unity";

  public bool dataChannelOpen { get; private set; }
  public event DelegateOnMessage OnMessage; // will be sent the bytes received, or null on close

  private RTCPeerConnection pc;
  private RTCDataChannel my_data_channel;
  private MediaStream receiveStream;
  private SignalChannel channel;
  private CancellationTokenSource listen_cts;
  private CancellationTokenSource channel_cts;
  private bool call_alive;

  void Start()
  {
    SignalChannel.InitIfNeeded(WebRtcMain.Instance.HttpServerAddress);
    WebRtcMain.Instance.InitIfNeeded();
    LogTag = (string.IsNullOrWhiteSpace(LogTag) ? this.GetType().Name : LogTag.Trim()) + ": ";
    localId ??= System.Net.Dns.GetHostName();

    listen_cts = new CancellationTokenSource();
    Task.Run(ListenAndAccept).ContinueWith(task =>
    {
      Debug.LogError(task.Exception.InnerException);
    }, TaskContinuationOptions.OnlyOnFaulted);
  }

  private void OnDestroy()
  {
    listen_cts?.Cancel();
    channel_cts?.Cancel();
    OnHangUp();
  }

  void ListenAndAccept()
  {
    if (string.IsNullOrWhiteSpace(localId))
      throw new ArgumentException("localId can't be null or empty");
    while (!listen_cts.IsCancellationRequested)
    {
      var listener = SignalChannel.Listen(localId, listen_cts.Token);
      listener.Wait();
      if (listen_cts.IsCancellationRequested)
        break;
      Log("Received incoming call");
      channel_cts = new CancellationTokenSource();
      this.channel = new SignalChannel(listener.Result.Item1, listener.Result.Item2, channel_cts.Token);
      call_alive = true;
      while (call_alive && !listen_cts.IsCancellationRequested)
        Thread.Sleep(1000);
      channel_cts.Cancel();
    }
  }

  void Update()
  {
    while (channel && channel.TryGet(out var msg))
    {
      if (msg.type == "offer" && pc == null)
      {
        Log("received offer");
        var desc = new RTCSessionDescription()
        {
          type = RTCSdpType.Offer,
          sdp = msg.sdp
        };
        Log("Got offer from " + channel.remote_mailbox);
        OnIncomingCall(desc);
      }
      else if (msg.type == "candidate" && pc != null)
      {
        pc.AddIceCandidate((RTCIceCandidate)msg);
      }
      // else, unknown msg
    }
  }

  void OnIncomingCall(RTCSessionDescription desc)
  {
    receiveStream = new MediaStream();
    receiveStream.OnAddTrack += OnAddTrack;

    var configuration = WebRtcMain.config;

    pc = new RTCPeerConnection(ref configuration)
    {
      OnIceCandidate = candidate => channel.send(new Request(candidate)),
      OnTrack = e => receiveStream.AddTrack(e.Track),
      OnIceConnectionChange = state => Log(LogTag + "Ice connection state " + state),
      OnIceGatheringStateChange = state => Log(LogTag + "Ice gathering state " + state),
      OnConnectionStateChange = state =>
      {
        Log("Connection state " + state);
        if (state == RTCPeerConnectionState.Failed ||
            state == RTCPeerConnectionState.Disconnected ||
            state == RTCPeerConnectionState.Closed)
        {
          OnHangUp();
        }
        else if (state == RTCPeerConnectionState.Connected)
          channel_cts.Cancel(); // no need to listen to the signalling channel anymore
      },
      OnDataChannel = data_channel =>
      {
        Log("The peer has created a data channel");
        my_data_channel = data_channel;
        my_data_channel.OnOpen = () => dataChannelOpen = true;
        my_data_channel.OnClose = () =>
        {
          dataChannelOpen = false;
          OnMessage?.Invoke(null);
          OnMessage = null;
        };
        my_data_channel.OnMessage = bytes => OnMessage?.Invoke(bytes);
      }
    };

    var transceiver = pc.AddTransceiver(TrackKind.Audio);
    transceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
    StartCoroutine(OnReceivedOffer(desc));
  }

  public void Send(byte[] buffer)
  {
    my_data_channel?.Send(buffer);
  }

  public void Send(string text)
  {
    my_data_channel?.Send(text);
  }

  public void OnPause()
  {
    var transceiver1 = pc.GetTransceivers().First();
    var track = transceiver1.Sender.Track;
    track.Enabled = false;
  }

  public void OnResume()
  {
    var transceiver1 = pc.GetTransceivers().First();
    var track = transceiver1.Sender.Track;
    track.Enabled = true;
  }

  void OnAddTrack(MediaStreamTrackEvent e)
  {
    var track = e.Track as AudioStreamTrack;
    outputAudioSource.SetTrack(track);
    outputAudioSource.loop = true;
    outputAudioSource.Play();
  }

  void OnHangUp()
  {
    outputAudioSource.Stop();
    receiveStream?.Dispose();
    my_data_channel?.Dispose();
    pc?.Dispose();
    pc = null;
    my_data_channel = null;
    dataChannelOpen = false;
    OnMessage = null;
    receiveStream = null;
    call_alive = false;
    Log("Hanged up call");
  }

  private IEnumerator OnReceivedOffer(RTCSessionDescription desc)
  {
    var op2 = pc.SetRemoteDescription(ref desc);
    yield return op2;
    if (op2.IsError)
    {
      var error = op2.Error;
      OnSetSessionDescriptionError(ref error);
      call_alive = false;
      yield break;
    }

    var op3 = pc.CreateAnswer();
    yield return op3;
    var answer_sdp = op3.Desc;
    pc.SetLocalDescription(ref answer_sdp);
    if (!op3.IsError)
    {
      Log("sending answer");
      channel.send(answer_sdp);
    }
    else
    {
      var error = op3.Error;
      OnSetSessionDescriptionError(ref error);
      call_alive = false;
    }
  }

  void OnSetSessionDescriptionError(ref RTCError error)
  {
    Debug.LogError($"{LogTag} Error Detail Type: {error.message}");
  }

  void Log(string msg)
  {
    UnityEngine.Debug.Log(LogTag + msg);
  }
}
