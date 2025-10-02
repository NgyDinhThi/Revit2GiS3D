using System;
using System.IO;
using System.Text;

class DumpGlbJson
{
    static void Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Usage: DumpGlbJson <path.glb>"); return; }
        string path = args[0];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (var br = new BinaryReader(fs))
        {
            uint magic = br.ReadUInt32();
            uint version = br.ReadUInt32();
            uint length = br.ReadUInt32();
            uint jsonLen = br.ReadUInt32();
            uint jsonType = br.ReadUInt32();
            byte[] jsonBytes = br.ReadBytes((int)jsonLen);
            string json = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0');
            Console.WriteLine(json);
        }
    }
}
