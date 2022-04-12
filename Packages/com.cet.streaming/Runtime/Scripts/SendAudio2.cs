using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.WebRTC;
using UnityEngine;

public class SendAudio2 : BaseForRtcConnection
{
  [Header("Input")]
  [SerializeField] private AudioSource inputAudioSource;
  [SerializeField] private string micDeviceName;

  int m_samplingFrequency = 48000;
  int m_lengthSeconds = 1;

  private DateTime disconnectTime = DateTime.MinValue;
  private bool connected;

  protected override void Start()
  {
    //ListMicrophones();
    base.Start();
  }

  void ListMicrophones()
  {
    foreach (var d in Microphone.devices)
      print(d);
  }

  protected override void BuildPeerConnection(string remote)
  {
    var codecs = RTCRtpSender.GetCapabilities(TrackKind.Audio).codecs;
    var excludeCodecTypes = new[] { "audio/CN", "audio/telephone-event" };
    List<RTCRtpCodecCapability> availableCodecs = new List<RTCRtpCodecCapability>();
    foreach (var codec in codecs)
    {
      if (excludeCodecTypes.Count(type => codec.mimeType.Contains(type)) > 0)
        continue;
      availableCodecs.Add(codec);
    }

    pc = base.BuildBasicPeerConnection(remote);
    pc.OnNegotiationNeeded = () => StartCoroutine(PrepareOffer(pc, remote));
    SetupDataChannel(pc.CreateDataChannel("channel 1"));

    var audioTrack = new AudioStreamTrack(inputAudioSource);
    pc.AddTrack(audioTrack);

    var transceiver = pc.GetTransceivers().First();
    var error = transceiver.SetCodecPreferences(availableCodecs.ToArray());
    if (error != RTCErrorType.None)
      Debug.LogError(error);
  }

  IEnumerator PrepareOffer(RTCPeerConnection pc, string remote)
  {
    var op = pc.CreateOffer();
    yield return op;
    if (op.IsError)
    {
      Log($"Error in creating offer: {op.Error.message}");
      yield break;
    }
    if (pc.SignalingState != RTCSignalingState.Stable)
      yield break;

    var desc = op.Desc;
    var op2 = pc.SetLocalDescription(ref desc);
    yield return op2;
    if (op2.IsError)
    {
      Log($"Error in setting the offer locally {op2.Error.message}");
      yield break;
    }
    var www = new put(remote, new Request(desc));
    yield return www;
    if (www.IsOk == false)
      Log($"Error in sending the offer to the remote");
  }

  protected override IEnumerator InitiateConnection()
  {
    Log("InitiateConnection");
    WebRtcMain.Instance.InitIfNeeded();
    if (string.IsNullOrEmpty(localId))
      localId = System.Net.Dns.GetHostName();

    if (pc != null && pc.ConnectionState != RTCPeerConnectionState.Closed)
    {
      Log("Previous connection is still alive");
      yield break;
    }
    //Hangup(); // hang up previous first
    if ((DateTime.Now - disconnectTime).TotalSeconds < 1)
      yield return new WaitForSeconds(1);

    bool first = true;
    IMsg www = null;
    string new_remoteId = null, local = null;
    Log("starting connection loop to " + remoteId);
    while (new_remoteId == null)
    {
      if (!first)
        yield return new WaitForSeconds(1);
      first = false;

      // extend an invitation to the remote peer
      local = localId + "_" + Guid.NewGuid().ToString();
      yield return (www = new put(remoteId, new Request { type = "connect", from = local }));
      if (!www.IsOk)
        continue; // signal-server not available
      yield return (www = new get(local, 10));
      if (!www.IsOk || www.answer?.type != "ok")
        continue; // signal-server or remote not responding
      new_remoteId = www.answer.switch_channel;
    }

    // at this point we have a remote that's ready to talk to us
    Log("Switched channel to " + new_remoteId);
    StartMicrophone();
    BuildPeerConnection(new_remoteId);

    while (www.IsOk && pc.ConnectionState != RTCPeerConnectionState.Connected)
    {
      yield return (www = new get(local));
      if (www.answer == null) continue; // TODO: how long to I keep trying before giving up?
      if (www.answer.type == "answer")
      {
        var desc = new RTCSessionDescription()
        {
          type = RTCSdpType.Answer,
          sdp = www.answer.sdp
        };
        StartCoroutine(pc.SetRemoteDescription(ref desc));
      }
      else if (www.answer.type == "candidate")
      {
        pc.AddIceCandidate((RTCIceCandidate)www.answer);
      }
    }
    connected = pc.ConnectionState == RTCPeerConnectionState.Connected;
    pc.OnConnectionStateChange += newstate =>
    {
      if (newstate == RTCPeerConnectionState.Closed || newstate == RTCPeerConnectionState.Failed)
        Hangup();
    };
  }

  protected override void OnAddTrack(MediaStreamTrackEvent e)
  {
    Log("On add track called??");
  }


  void StartMicrophone()
  {
    var clipInput = Microphone.Start(micDeviceName, true, m_lengthSeconds, m_samplingFrequency);
    // set the latency to “0” samples before the audio starts to play.
    while (!(Microphone.GetPosition(micDeviceName) > 0)) { }

    inputAudioSource.loop = true;
    inputAudioSource.clip = clipInput;
    inputAudioSource.Play();
  }

  [ContextMenu("Hang up")]
  protected override void Hangup()
  {
    bool was_connected = connected;
    inputAudioSource.Stop();
    base.Hangup();
    disconnectTime = DateTime.Now;
    connected = false;
    if (was_connected && keepAlive)
      StartCoroutine(InitiateConnection());
  }
}
