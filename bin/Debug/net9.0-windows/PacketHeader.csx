#r "nuget: Newtonsoft.Json, 13.0.4"
using System;
using Newtonsoft.Json;
using ProtoTestTool.ScriptContract;

public class Header : IHeader
{
    public int MsgId { get; set; }
    public byte Flags { get; set; }
    
    public string ToJsonString()
    {
        return JsonConvert.SerializeObject(this);
    }
}