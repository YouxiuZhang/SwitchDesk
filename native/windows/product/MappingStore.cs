using System;
using System.IO;
using System.Web.Script.Serialization;
namespace SwitchDesk.Desktop {
    internal sealed class SavedMapping { public string Id; public string Name; public int Original; public int Target; public bool Confirmed; }
    internal static class MappingStore {
        private static string FileName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk", "mapping.json"); } }
        internal static SavedMapping Load() { try { return new JavaScriptSerializer().Deserialize<SavedMapping>(File.ReadAllText(FileName)); } catch { return null; } }
        internal static void Save(SwitchState state) {
            if (!state.UserConfirmed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            string temp = FileName + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(new SavedMapping { Id = state.DisplayId, Name = state.DisplayName, Original = state.OriginalInput, Target = state.TargetInput, Confirmed = true }));
            if (File.Exists(FileName)) File.Delete(FileName);
            File.Move(temp, FileName);
        }
    }
}
