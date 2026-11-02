using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace RemotePrint.Setup;

internal static class SetupDefaults
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("REMOTEPRINT-SETUP-CONFIG-V1");

    internal static string ServerUrl() => ReadServerUrl(Assembly.GetExecutingAssembly().Location);

    internal static string ReadServerUrl(string executable)
    {
        // Generic dist builds have no overlay and keep the URL field editable/empty.
        // Malformed configuration also falls back to manual entry, never to execution.
        try
        {
            using var input = File.OpenRead(executable);
            var footerSize = Marker.Length + sizeof(int);
            if (input.Length < footerSize) return "";
            input.Seek(-footerSize, SeekOrigin.End);
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            var length = reader.ReadInt32();
            if (!reader.ReadBytes(Marker.Length).SequenceEqual(Marker) || length <= 0 || length > 4096 || length > input.Length - footerSize) return "";
            input.Seek(-footerSize - length, SeekOrigin.End);
            var data = new JavaScriptSerializer { MaxJsonLength = 4096 }.Deserialize<Dictionary<string, string>>(
                new UTF8Encoding(false, true).GetString(reader.ReadBytes(length)));
            if (data == null || !data.TryGetValue("serverUrl", out var server) || string.IsNullOrWhiteSpace(server)) return "";
            return Bootstrapper.ServerUri(server).AbsoluteUri.TrimEnd('/');
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
        { return ""; }
    }
}
