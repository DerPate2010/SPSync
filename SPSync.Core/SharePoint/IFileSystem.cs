using System;
using System.Collections.Generic;

namespace SPSync.Core
{
    public interface IFileSystem
    {
        void DeleteFile(int id);
        void RenameItem(int id, string newName, string relativePath, string name, bool isFolder);
        void InitChangeTokenIfNecessary(Metadata.MetadataStore metadataStore);
        List<SharePointItem> GetChangedFiles(Metadata.MetadataStore metadataStore, Action<int, string> progressHandler, out string newChangeToken);
        List<SharePointItem> DownloadFileList();
        DateTime GetFileTimestamp(string relativeFile, out int eTag);
        void DownloadFile(string filename, string targetDirectory, DateTime modifiedDate);
        int CreateFolder(string relativePath, string folderName);
        void DeleteFolder(string relativePath, string folderName);
        int UploadFile(string relativeFile, string localFile);
        void CreateFoldersIfNotExists(string relFolder);
    }
}