using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

public class ReceiveAudio2 : BaseForRtcConnection
{
  [Header("Output")]
  [SerializeField] private AudioSource outputAudioSource;

  protected override void OnAddTrack(MediaStreamTrackEvent e)
  {
    var track = e.Track as AudioStreamTrack;
    outputAudioSource.SetTrack(track);
    outputAudioSource.loop = true;
    outputAudioSource.Play();
  }

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
    IMsg www = null;
    Log("Listening to incoming connections for audio...");

    // remove old requests for this machine
    using (var del = UnityWebRequest.Delete(localId))
      yield return del.SendWebRequest();

    while (true)
    {
      do
        yield return www = new get(localId);
      while (www.IsOk == false);
      var new_local_channel = localId + "_" + Guid.NewGuid().ToString();
      var remote = www.answer.from;
      Log($"Accepted connection from {remote}, sending response now...");
      yield return (www = new put(remote, new Request { type = "ok", switch_channel = new_local_channel }));
      Log("Sent the response: " + www.Result);
      if (www.IsOk)
      {
        Log($"ReceiveAudio2 is switching to {new_local_channel}");
        var accepter = StartCoroutine(Accept(new_local_channel, remote));
        yield return new WaitForSeconds(15);
        // don't accept 2 concurrent connections
        yield return new WaitUntil(() => pc == null || pc.ConnectionState != RTCPeerConnectionState.Connected);
        StopCoroutine(accepter); // in case we're still listening, with no connection in sight, stop
      }
    }
  }

  IEnumerator Accept(string local, string remote)
  {
    IMsg www = null;
    List<RTCIceCandidate> pending = null;
    while (pc == null || pc.ConnectionState != RTCPeerConnectionState.Connected)
    {
      yield return www = new get(local, 10);
      if (www.IsOk == false)
      {
        Log("No messages from remote for 10 seconds; aborting");
        Hangup(pc);
        pc = null;
        yield break;
      }
      if (www.answer?.type == "offer" && pc == null)
      {
        var desc = new RTCSessionDescription()
        {
          type = RTCSdpType.Offer,
          sdp = www.answer.sdp
        };
        BuildPeerConnection(remote);
        StartCoroutine(OnReceivedOffer(remote, desc));
        if (pending != null)
        {
          foreach (var p in pending)
            pc.AddIceCandidate(p);
          pending.Clear();
        }
      }
      if (www.answer?.type == "candidate")
      {
        if (pc != null)
          pc.AddIceCandidate((RTCIceCandidate)www.answer);
        else
        {
          pending ??= new List<RTCIceCandidate>();
          pending.Add((RTCIceCandidate)www.answer);
        }
      }
    }
    if (pc != null)
      Log(pc.ConnectionState.ToString());
  }

  protected override void BuildPeerConnection(string remote)
  {
    pc = BuildBasicPeerConnection(remote);
    var transceiver = pc.AddTransceiver(TrackKind.Audio);
    transceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
  }

  private IEnumerator OnReceivedOffer(string remote, RTCSessionDescription desc)
  {
    var op = pc.SetRemoteDescription(ref desc);
    yield return op;
    if (op.IsError)
    {
      Log($"Error in setting local description: {op.Error.message}");
      yield break;
    }

    var op2 = pc.CreateAnswer();
    yield return op2;
    var answer_sdp = op2.Desc;
    pc.SetLocalDescription(ref answer_sdp);
    if (op2.IsError)
    {
      Log($"Error in setting local description: {op.Error.message}");
      yield break;
    }

    Log("sending answer");
    yield return new put(remote, new Request(answer_sdp));
  }

  protected override void Hangup(RTCPeerConnection pc)
  {
    outputAudioSource.Stop();
    base.Hangup(pc);
    pc = null;
  }
}
