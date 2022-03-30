using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

public class VideoAudioReceiver : BaseForRtcConnection
{
  [Header("Output")]
  [SerializeField] private AudioSource outputAudioSource;
  [SerializeField] private RawImage outputVideo;
  [SerializeField] private Material alternativeOutput;

  public void Send(byte[] buffer)
  {
    data_channel?.Send(buffer);
  }

  public void Send(string text)
  {
    data_channel?.Send(text);
  }

  protected override IEnumerator InitiateConnection()
  {
    WebRtcMain.Instance.InitIfNeeded();
    if (string.IsNullOrEmpty(localId))
      localId = System.Net.Dns.GetHostName();

    Hangup(pc); // hang up previous first
    pc = null;

    var local = localId + "_" + Guid.NewGuid().ToString();
    bool first = true;
    IMsg www = null;
    string new_remoteId = null;
    Log("starting connection loop to " + remoteId);
    while (new_remoteId == null)
    {
      if (!first)
        yield return new WaitForSeconds(1);
      first = false;

      // extend an invitation to the remote peer
      yield return (www = new put(remoteId, new Request { type = "connect", from = local }));
      if (!www.IsOk) 
        continue; // signal-server not available
      yield return (www = new get(local));
      if (!www.IsOk || www.answer?.type != "ok")
        continue; // signal-server or remote not responding
      new_remoteId = www.answer.switch_channel;
    }

    // at this point we have a remote that's ready to talk to us
    Log("Switched channel to " + new_remoteId);
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
        StartCoroutine(OnAnswer(pc, desc));
      }
      else if (www.answer.type == "candidate")
      {
        pc.AddIceCandidate((RTCIceCandidate)www.answer);
      }
    }

    if (keepAlive)
      pc.OnConnectionStateChange += newstate => StartCoroutine(InitiateConnection());
  }

  IEnumerator PrepareOffer(RTCPeerConnection pc, string remoteUrl)
  {
    var op1 = pc.CreateOffer();
    yield return op1;
    if (op1.IsError)
    {
      Log("error: create offer");
      yield break;
    }
    var desc = op1.Desc;
    yield return pc.SetLocalDescription(ref desc);
    Log("created the offer and set it as local. now sending it...");
    yield return new put(remoteUrl, new Request(desc));
  }

  IEnumerator OnAnswer(RTCPeerConnection pc, RTCSessionDescription desc)
  {
    Log("got answer");
    var op1 = pc.SetRemoteDescription(ref desc);
    yield return op1;
    if (op1.IsError)
    {
      Log("error in setting answer " + op1.Error.errorType + " " + op1.Error.message);
      yield break;
    }
  }

  protected override void BuildPeerConnection(string remoteId)
  {
    pc = BuildBasicPeerConnection(remoteId);

    var vid1 = pc.AddTransceiver(TrackKind.Video);
    vid1.Direction = RTCRtpTransceiverDirection.RecvOnly;
    vid1.SetCodecPreferences(RTCRtpReceiver.GetCapabilities(TrackKind.Video).codecs);

    var audio1 = pc.AddTransceiver(TrackKind.Audio);
    audio1.Direction = RTCRtpTransceiverDirection.RecvOnly;
    audio1.SetCodecPreferences(RTCRtpSender.GetCapabilities(TrackKind.Audio).codecs);

    pc.OnNegotiationNeeded = () => StartCoroutine(PrepareOffer(pc, remoteId));
  }

  protected override void OnAddTrack(MediaStreamTrackEvent e)
  {
    Log("received track " + e.Track.Kind);
    if (e.Track is VideoStreamTrack video)
    {
      video.OnVideoReceived += tex =>
      {
        if (outputVideo != null) outputVideo.texture = tex;
        if (alternativeOutput != null) alternativeOutput.mainTexture = tex;
      };
    }
    if (e.Track is AudioStreamTrack audio)
    {
      outputAudioSource.SetTrack(audio);
      outputAudioSource.loop = true;
      outputAudioSource.Play();
    }
  }

  [ContextMenu("Hang up")]
  protected override void Hangup(RTCPeerConnection pc_)
  {
    base.Hangup(this.pc);
    outputAudioSource.Stop();
    // erase the texture? disable the RawImage?
    this.pc = null;
  }

  [ContextMenu("Call")]
  public void CallAgain()
  {
    StartCoroutine(InitiateConnection());
  }
}
