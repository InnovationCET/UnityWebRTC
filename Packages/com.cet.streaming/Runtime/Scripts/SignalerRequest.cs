using System;
using Unity.WebRTC;

[Serializable]
public class Request : EventArgs
{
  public string type;
  public long timestamp;
  public string from;
  public string switch_channel;

  public string sdp;

  // ICE candidate properties
  public int SdpMLineIndex;
  public string SdpMid;
  public string candidate;
  //old format
  public string id;
  public int label;

  public Request()
  {
    timestamp = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();
  }

  public Request(RTCIceCandidate c) : base()
  {
    type = "candidate";
    candidate = c.Candidate;
    id = SdpMid = c.SdpMid;
    label = SdpMLineIndex = c.SdpMLineIndex ?? 0;
  }
  public Request(RTCSessionDescription desc) : base()
  {
    type = desc.type.ToString().ToLower();
    sdp = desc.sdp;
  }

  public override string ToString() =>
    type == "connect" ? $"Connect request. local channel = {from}" :
    type == "ok" ? $"Connection accepted from {switch_channel}" :
    type == "offer" ? $"Offer: {sdp}" :
    type == "answer" ? $"Answer: {sdp}" :
    type == "candidate" ? $"ICE candidate" :
    "Unknown msg type";

  public static explicit operator RTCIceCandidate(Request v)
  {
    var result = new RTCIceCandidate(new RTCIceCandidateInit { candidate = v.candidate, sdpMid = v.SdpMid, sdpMLineIndex = v.SdpMLineIndex });
    return result;
  }
}
