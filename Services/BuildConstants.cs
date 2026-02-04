namespace ProtoTestTool.Services
{
    public static class BuildConstants
    {
        public const string ScriptDllName = "Script.dll";
        public const string ProtosDllName = "Protos.dll";
        
        public const string ScriptsFolder = "Scripts";
        public const string ProtosFolder = "Protos";
        public const string ProtoGenFolder = "ProtoGen";
        public const string LibsFolder = "Libs";

        public const string FileNameWorkspaceConfig = "workspace_config.json";
        public const string FileNamePacketCodec = "PacketCodec.cs";
        public const string FileNamePacketRegistry = "PacketRegistry.cs";
        public const string FileNamePacketHeader = "PacketHeader.cs";
        public const string FileNamePacketInterceptor = "PacketInterceptor.cs";
        public const string FileNameReadme = "readme.txt";

        public const string TemplatePacketCodec = "PacketCodec";
        public const string TemplatePacketRegistry = "PacketRegistry";
        public const string TemplatePacketHeader = "PacketHeader";
        public const string TemplatePacketInterceptor = "PacketInterceptor";

        public const string ReadmeContent = "Place your .proto files in this directory.\nThey will be automatically compiled and loaded.";
    }
}
