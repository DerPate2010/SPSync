using PInvoke;
using SPSync.Core.Common;

namespace SPSync.Core
{
    public class UsnEntry
    {
        public ulong FileRefNumber { get; set; }
        public string Path { get; set; }
        public string NewPath { get; set; }
        public Win32Api.UsnReason ChangeType { get; set; }
        public ItemType Type { get; set; }
    }
}