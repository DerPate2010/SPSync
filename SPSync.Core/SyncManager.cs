﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using SPSync.Core.Metadata;
using SPSync.Core.Common;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;

namespace SPSync.Core
{
    public class SyncManager
    {
        private string _localFolder;
        private string _originalFolder;
        private SyncConfiguration _configuration;
        private SharePointManager _sharePointManager;
        private MetadataStore _metadataStore;

        public event EventHandler<SyncProgressEventArgs> SyncProgress;
        public event EventHandler<ItemProgressEventArgs> ItemProgress;
        public event EventHandler<SyncProgressEventArgs> ChangesProgress;
        public event EventHandler<ConflictEventArgs> ItemConflict;

        public string ConfigurationName => _configuration.Name;

        protected void OnSyncProgress(int percent, ProgressStatus status, string message = "", Exception innerException = null)
        {
            SyncProgress?.Invoke(this, new SyncProgressEventArgs(_configuration, percent, status, message, innerException));
        }

        protected void OnMetadataProgress(int percent, ItemType type, ProgressStatus status, string message = "", Exception innerException = null)
        {
            ItemProgress?.Invoke(this, new ItemProgressEventArgs(_configuration, percent, type, status, message, innerException));
        }

        protected void OnChangesProgress(int percent, ProgressStatus status, string message = "", Exception innerException = null)
        {
            ChangesProgress?.Invoke(this, new SyncProgressEventArgs(_configuration, percent, status, message, innerException));
        }

        protected ItemStatus OnItemConflict(MetadataItem item)
        {
            if (ItemConflict != null)
            {
                var arg = new ConflictEventArgs(_configuration, item.DeepClone());
                ItemConflict(this, arg);
                return arg.NewStatus;
            }
            return ItemStatus.Conflict;
        }

        public SyncManager(string localFolder)
        {
            _originalFolder = localFolder;
            _configuration = SyncConfiguration.FindConfiguration(localFolder);
            _localFolder = _configuration.LocalFolder;
            _sharePointManager = _configuration.GetSharePointManager();
            _metadataStore = new MetadataStore(_localFolder);
        }

        public MetadataItem[] SyncResults { get; private set; }

        public void DownloadFile(string syncFileName)
        {
            string fullSyncFile = Path.Combine(_originalFolder, syncFileName);
            string origFilename = Path.GetFileNameWithoutExtension(syncFileName);
            _sharePointManager.DownloadFile(origFilename, _originalFolder, File.GetLastWriteTimeUtc(fullSyncFile));
            File.Delete(fullSyncFile);
        }

        public async Task SynchronizeAsync(bool reviewOnly = false, bool rescanLocalFiles = true)
        {
            await Task.Run(() =>
            {
                lock (this)
                {
                    Synchronize(reviewOnly, rescanLocalFiles);
                }
            });
        }

        public void WatchChanges()
        {
            //USN Journal
            //Sharepoint changes
        }

        public void SyncMetadatStoreIfNecessary()
        {
            SyncMetadataStore(false, _configuration.ConflictHandling, true);
        }

        public void RunSynchronization()
        {
            var countChanged = 1;

            lock (this)
            {
                OnSyncProgress(0, ProgressStatus.Analyzing);

                try
                {
                    countChanged = _metadataStore.GetItemsToProcess();

                    SyncChanges(countChanged);

                    OnSyncProgress(100, ProgressStatus.Completed);
                }
                catch (Exception ex)
                {
                    //todo:
                    if (_configuration.AuthenticationType == AuthenticationType.ADFS) //&& ex is webexception 403
                    {
                        Adfs.AdfsHelper.InValidateCookie();
                    }
                    OnSyncProgress(100, ProgressStatus.Error, "An error has occured: " + ex.Message, ex);
                }
            }
        }

        public int Synchronize(bool reviewOnly = false, bool rescanLocalFiles = true)
        {
            var countChanged = 1;

            lock (this)
            {
                OnSyncProgress(0, ProgressStatus.Analyzing);

                try
                {
                    SyncMetadataStore(reviewOnly, _configuration.ConflictHandling, rescanLocalFiles);
                    countChanged = _metadataStore.GetItemsToProcess();

                    if (reviewOnly || countChanged < 1)
                    {
                        OnSyncProgress(100, ProgressStatus.Completed);
                        return countChanged;
                    }

                    SyncChanges(countChanged);

                    OnSyncProgress(100, ProgressStatus.Completed);
                }
                catch (Exception ex)
                {
                    //todo:
                    if (_configuration.AuthenticationType == AuthenticationType.ADFS) //&& ex is webexception 403
                    {
                        Adfs.AdfsHelper.InValidateCookie();
                    }
                    OnSyncProgress(100, ProgressStatus.Error, "An error has occured: " + ex.Message, ex);
                    return -1;
                }
            }

            return countChanged;
        }

        public void SynchronizeLocalFileChange(string fullPath, FileChangeType changeType, string oldFullPath)
        {
            lock (this)
            {
                Logger.LogDebug("SynchronizeLocalFileChange Path={0} ChangeType={1} OldPath={2}", fullPath, changeType, oldFullPath);

                OnChangesProgress(0, ProgressStatus.Analyzing);

                try
                {
                    var syncToRemote = (_configuration.Direction == SyncDirection.LocalToRemote || _configuration.Direction == SyncDirection.Both);
                    var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;

                    if (!syncToRemote)
                    {
                        OnChangesProgress(100, ProgressStatus.Completed);
                        return;
                    }

                    if (!_configuration.ShouldFileSync(fullPath))
                    {
                        OnChangesProgress(100, ProgressStatus.Completed);
                        return;
                    }

                    var localExtension = Path.GetExtension(fullPath);
                    if (localExtension == ".spsync")
                    {
                        OnChangesProgress(100, ProgressStatus.Completed);
                        return;
                    }

                    var isDirectory = false;

                    if (changeType != FileChangeType.Deleted)
                    {
                        try
                        {
                            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden))
                            {
                                OnChangesProgress(100, ProgressStatus.Completed);
                                return;
                            }

                            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory))
                                isDirectory = true;

                            if (isDirectory)
                            {
                                if (Path.GetDirectoryName(fullPath) == MetadataStore.STOREFOLDER)
                                {
                                    OnChangesProgress(100, ProgressStatus.Completed);
                                    return;
                                }
                            }
                            else
                            {
                                if (Directory.GetParent(fullPath).Name == MetadataStore.STOREFOLDER)
                                {
                                    OnChangesProgress(100, ProgressStatus.Completed);
                                    return;
                                }
                            }
                        }
                        catch
                        {
                            OnChangesProgress(100, ProgressStatus.Completed);
                            return;
                        }
                    }

                    MetadataItem item = null;

                    if (string.IsNullOrEmpty(oldFullPath))
                    {
                        item = _metadataStore.GetByFileName(fullPath);
                    }
                    else
                    {
                        item = _metadataStore.GetByFileName(oldFullPath);
                        if (item == null)
                        {
                            changeType = FileChangeType.Changed;
                            item = _metadataStore.GetByFileName(fullPath);
                        }
                    }

                    if (item == null)
                    {
                        if (changeType != FileChangeType.Deleted)
                        {
                            if (_metadataStore.GetByFileName(fullPath) == null)
                                _metadataStore.Add(new MetadataItem(fullPath, isDirectory ? ItemType.Folder : ItemType.File));
                        }
                    }
                    else
                    {
                        item.UpdateWithLocalInfo(_configuration.ConflictHandling, Guid.NewGuid());
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);

                        if (changeType == FileChangeType.Renamed)
                        {
                            item.NewNameAfterRename = Path.GetFileName(fullPath); //works for directories as well
                            item.Status = ItemStatus.RenamedLocal;

                            if (isDirectory)
                            {
                                foreach (var itemInFolder in _metadataStore.ItemsInDirSub(item.LocalFile))
                                {
                                    if (itemInFolder.Id == item.Id)
                                        continue;
                                    itemInFolder.LocalFolder = itemInFolder.LocalFolder.Replace(item.LocalFile, fullPath);
                                }
                            }
                        }
                    }

                    if (changeType == FileChangeType.Deleted && item != null)
                        item.Status = ItemStatus.DeletedLocal;

                    _metadataStore.Save();

                    SyncChanges(1);

                    OnChangesProgress(100, ProgressStatus.Completed);
                }
                catch (Exception ex)
                {
                    //todo:
                    if (_configuration.AuthenticationType == AuthenticationType.ADFS) //&& ex is webexception 403
                    {
                        Adfs.AdfsHelper.InValidateCookie();
                    }
                    OnChangesProgress(100, ProgressStatus.Error, "An error has occured: " + ex.Message, ex);
                    return;
                }
            }
        }

        private void SyncMetadataStore(bool doNotSave, ConflictHandling conflictHandling, bool rescanLocalFiles = true)
        {
            var sumWatch = Stopwatch.StartNew();

            var correlationId = Guid.NewGuid();

            //reset item status for all items except the ones with errors
            _metadataStore.ResetExceptErrors();

            var watch = Stopwatch.StartNew();

            watch.Stop();

            var syncToRemote = (_configuration.Direction == SyncDirection.LocalToRemote || _configuration.Direction == SyncDirection.Both);
            var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;

            var searchOption = SearchOption.AllDirectories;

            if (rescanLocalFiles)
            {
                OnMetadataProgress(0,ItemType.Unknown, ProgressStatus.Analyzing, "Processing local changes");

                #region Iterate local files/folders

                watch = Stopwatch.StartNew();

                Parallel.ForEach(Directory.EnumerateDirectories(_localFolder, "*", searchOption), localFolder =>
                {
                    if (!syncToRemote)
                        return;

                    if (!_configuration.ShouldFileSync(localFolder))
                        return;

                    if (File.GetAttributes(localFolder).HasFlag(FileAttributes.Hidden))
                        return;

                    if (Path.GetDirectoryName(localFolder) == MetadataStore.STOREFOLDER)
                        return;

                    var item = _metadataStore.GetByFileName(localFolder);
                    if (item == null)
                    {
                        _metadataStore.Add(new MetadataItem(localFolder, ItemType.Folder));
                    }
                    else
                    {
                        item.UpdateWithLocalInfo(conflictHandling, correlationId);
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                });

                watch.Stop();
                watch = Stopwatch.StartNew();

                // update store for local files
                Parallel.ForEach(Directory.EnumerateFiles(_localFolder, "*.*", searchOption), localFile =>
                //foreach (var localFile in Directory.EnumerateFiles(_localFolder, "*.*", searchOption))
                {
                    if (!syncToRemote)
                        return;

                    if (!_configuration.ShouldFileSync(localFile))
                        return;

                    var localExtension = Path.GetExtension(localFile);
                    if (localExtension == ".spsync")
                        return;

                    if (File.GetAttributes(localFile).HasFlag(FileAttributes.Hidden))
                        return;

                    if (Directory.GetParent(localFile).Name == MetadataStore.STOREFOLDER)
                        return;

                    var item = _metadataStore.GetByFileName(localFile);
                    if (item == null)
                    {
                        _metadataStore.Add(new MetadataItem(localFile, ItemType.File));
                    }
                    else
                    {
                        item.UpdateWithLocalInfo(conflictHandling, correlationId);
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                });

                watch.Stop();

                #endregion
            }

            #region Iterate remote files/folders

            var remoteFileList = _sharePointManager.GetChangedFiles(_metadataStore, (percent, currentFile) =>
            {
                OnMetadataProgress(percent, ItemType.Unknown, ProgressStatus.Analyzing, $"Processing remote changes... '{currentFile}'");
            });

            // update store for remote files/folders
            foreach (var remoteItem in remoteFileList)
            {
                if (!syncToLocal)
                    continue;

                var localFile = new DirectoryInfo(Path.Combine(_localFolder, remoteItem.FullFileName)).FullName;

                if (!_configuration.ShouldFileSync(localFile))
                    continue;

                string fn = localFile;
                if (remoteItem.Type == ItemType.Folder)
                    fn = Path.Combine(localFile, remoteItem.Name);

                var item = _metadataStore.GetByItemId(remoteItem.Id);
                if (remoteItem.ChangeType == Microsoft.SharePoint.Client.ChangeType.Add)
                {
                    if (item == null)
                    {
                        item = _metadataStore.GetByFileName(localFile);
                    }
                    // new
                    if (item == null)
                    {
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name, Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
                    }
                    // remote and local change
                    else
                    {
                        item.UpdateWithRemoteInfo(remoteItem.Id, remoteItem.ETag, remoteItem.LastModified, conflictHandling, correlationId);
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                }
                if (remoteItem.ChangeType == Microsoft.SharePoint.Client.ChangeType.DeleteObject)
                {
                    if (item != null)
                        item.Status = ItemStatus.DeletedRemote;
                }
                if (remoteItem.ChangeType == Microsoft.SharePoint.Client.ChangeType.Rename)
                {
                    if (item == null)
                    {
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name, Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
                    }
                    else
                    {
                        if (item.Name != remoteItem.Name)
                        {
                            item.NewNameAfterRename = remoteItem.Name;
                            item.Status = ItemStatus.RenamedRemote;

                            if (item.Type == ItemType.Folder)
                            {
                                foreach (var itemInFolder in _metadataStore.ItemsInDirSub(item.LocalFile))
                                {
                                    if (itemInFolder.Id == item.Id)
                                        continue;
                                    var newFolder = _localFolder + remoteItem.FullFileName.Substring(1);
                                    itemInFolder.LocalFolder = itemInFolder.LocalFolder.Replace(item.LocalFile, newFolder);
                                    itemInFolder.HasError = true;
                                }
                            }
                        }
                        else
                        {
                            item.Status = ItemStatus.Unchanged;
                        }
                    }
                }
                if (remoteItem.ChangeType == Microsoft.SharePoint.Client.ChangeType.Update)
                {
                    // new
                    if (item == null)
                    {
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name, Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
                    }
                    else
                    {
                        item.UpdateWithRemoteInfo(remoteItem.Id, remoteItem.ETag, remoteItem.LastModified, conflictHandling, correlationId);
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                }
            }

            #endregion

            #region Check for deleted files/folders

            var itemsToDelete = new List<Guid>();

            // update store: files
            foreach (var item in _metadataStore.ItemsUnchangedNoError(ItemType.File))
            {
                var path = item.LocalFile;

                if (!_configuration.ShouldFileSync(path))
                    continue;

                if (!File.Exists(path) && !File.Exists(path + ".spsync"))
                {
                    item.Status = ItemStatus.DeletedLocal;
                }
                //if (remoteFileList.Count(p => p.Id == item.SharePointId) < 1)
                //{
                //    if (item.Status == ItemStatus.DeletedLocal)
                //    {
                //        itemsToDelete.Add(item.Id);
                //    }
                //    item.Status = ItemStatus.DeletedRemote;
                //}
            }

            // update store: folders
            foreach (var item in _metadataStore.ItemsUnchangedNoError(ItemType.Folder))
            {
                var relFile = item.LocalFile.Replace(_localFolder, string.Empty).TrimStart('.', '\\');
                var path = item.LocalFile;

                if (!_configuration.ShouldFileSync(path))
                    continue;

                if (!Directory.Exists(path))
                {
                    item.Status = ItemStatus.DeletedLocal;
                        
                    // delete all dependend items
                    _metadataStore.ItemsInDir(item.LocalFile).ToList().ForEach(p => { if (!itemsToDelete.Contains(p.Id)) itemsToDelete.Add(p.Id); });
                }
                //if (remoteFileList.Count(p => p.FullFileName.Replace(_localFolder, string.Empty).TrimStart('.', '\\') + p.Name == relFile) < 1)
                //{
                //    if (item.Status == ItemStatus.DeletedLocal)
                //    {
                //        if (!itemsToDelete.Contains(item.Id))
                //            itemsToDelete.Add(item.Id);
                //    }
                //    item.Status = ItemStatus.DeletedRemote;
                //}
            }

            #endregion

            itemsToDelete.ForEach(p => _metadataStore.Delete(p));

            _metadataStore.ItemsChanged().ToList().ForEach(p =>
            {
                Logger.LogDebug(correlationId, p.Id, "(Result) Item Name={0}, Status={1}, HasError={2}, LastError={3}", p.Name, p.Status, p.HasError, p.LastError);
            });

            // reset error flag
            //_metadataStore.Items.Where(p => p.HasError && p.Status != ItemStatus.Unchanged).ToList().ForEach(p => p.HasError = false);

            if (!doNotSave)
                _metadataStore.Save();

            sumWatch.Stop();

        }

        private void SyncChanges(int countChanged)
        {

            List<Guid> itemsToDelete = new List<Guid>();

            var syncToRemote = (_configuration.Direction == SyncDirection.LocalToRemote || _configuration.Direction == SyncDirection.Both);
            var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;

            int countProcessed=0;
            
            MetadataItem item;
            while (_metadataStore.GetNextItemToProcess(out item))
            {
                countProcessed++;
                OnSyncProgress((int)(((double)countProcessed / (double)countChanged) * 100),
                    ProgressStatus.Running, string.Format("{1} {0}...", item.Name, GetLogMessage(item)));

                bool itemToDelete =false;
                if (item.Type == ItemType.Folder)
                {
                    ProcessFolder(item, syncToRemote, syncToLocal, out itemToDelete);
                }
                else if (item.Type == ItemType.File)
                {
                    ProcessFile(item, syncToRemote, syncToLocal, out itemToDelete);
                }
                if (itemToDelete)
                {
                    _metadataStore.Delete(item.Id);
                }
            }


            OnSyncProgress(90, ProgressStatus.Running, "Finalizing...");

            itemsToDelete.ForEach(p => _metadataStore.Delete(p));

            _metadataStore.Save();
        }

        private string GetLogMessage(MetadataItem item)
        {
            switch (item.Status)
            {
                case ItemStatus.UpdatedLocal: return "Updating remote file";
                case ItemStatus.UpdatedRemote: return "Updating local file";
                case ItemStatus.DeletedLocal: return "Deleting remote file";
                case ItemStatus.DeletedRemote: return "Deleting local file";
                case ItemStatus.RenamedLocal: return "Renaming remote file";
                case ItemStatus.RenamedRemote: return "Renaming local file";
                default: return "Processing";
            }
        }

        private bool ProcessFile(MetadataItem item, bool syncToRemote, bool syncToLocal, out bool itemToDelete)
        {
            itemToDelete = false;

            try
            {
                var relFolder = item.LocalFolder.Replace(_localFolder, string.Empty).TrimStart('.', '\\');
                var relFile = item.LocalFile.Replace(_localFolder, string.Empty).TrimStart('.', '\\');
                if (item.Status == ItemStatus.UpdatedLocal && syncToRemote)
                {
                    // if not exists anymore (deleted between metadata and real sync)
                    if (!File.Exists(item.LocalFile))
                    {
                        item.Status = ItemStatus.DeletedLocal;
                        return true;
                    }

                    try
                    {
                        _sharePointManager.CreateFoldersIfNotExists(relFolder);
                        int id = _sharePointManager.UploadFile(relFile, item.LocalFile);
                        Logger.LogDebug("File uploaded with id: {0}", id);

                        int etag;
                        var remoteTimestamp = _sharePointManager.GetFileTimestamp(relFile, out etag);
                        item.ETag = etag;
                        Logger.LogDebug("Got ETag from remote file: {0}", etag);

                        item.SharePointId = id;
                        item.LastModified = File.GetLastWriteTimeUtc(item.LocalFile);
                        item.Status = ItemStatus.Unchanged;
                    }
                    catch (IOException ex)
                    {
                        OnLockedFile(item);
                    }
                }
                else if (item.Status == ItemStatus.UpdatedRemote && syncToLocal)
                {
                    string fullNameNotSynchronized = item.LocalFile + ".spsync";

                    int etag;
                    var remoteTimestamp = _sharePointManager.GetFileTimestamp(relFile, out etag);
                    item.ETag = etag;
                    item.LastModified = remoteTimestamp;

                    try
                    {
                        if (File.Exists(item.LocalFile))
                        {
                            _sharePointManager.DownloadFile(Path.GetFileName(item.LocalFile),
                                Path.GetDirectoryName(item.LocalFile), remoteTimestamp);
                            item.Status = ItemStatus.Unchanged;
                            return true;
                        }

                        if (File.Exists(fullNameNotSynchronized))
                        {
                            item.Status = ItemStatus.Unchanged;
                            return true;
                        }

                        CreateFoldersIfNotExists(item.LocalFolder);

                        if (_configuration.DownloadHeadersOnly)
                        {
                            File.Create(fullNameNotSynchronized).Close();
                            File.SetLastWriteTimeUtc(fullNameNotSynchronized, remoteTimestamp);
                        }
                        else
                        {
                            _sharePointManager.DownloadFile(Path.GetFileName(item.LocalFile),
                                Path.GetDirectoryName(item.LocalFile), remoteTimestamp);
                        }

                        item.Status = ItemStatus.Unchanged;
                    }
                    catch (System.Net.WebException ex)
                    {
                        if (ex.InnerException != null && ex.InnerException is IOException)
                        {
                            OnLockedFile(item);
                        }
                        else
                        {
                            OnErrorProcessing(item, ex);
                        }
                    }
                }
                else if (item.Status == ItemStatus.DeletedLocal && syncToRemote)
                {
                    try
                    {
                        _sharePointManager.DeleteFile(item.SharePointId);
                    }
                    catch (Exception ex)
                    {
                    }
                    itemToDelete = true;
                }
                else if (item.Status == ItemStatus.DeletedRemote && syncToLocal)
                {
                    try
                    {
                        if (File.Exists(item.LocalFile))
                            FileHelper.RecycleFile(item.LocalFile);
                        if (File.Exists(item.LocalFile + ".spsync"))
                            FileHelper.RecycleFile(item.LocalFile + ".spsync");

                        itemToDelete = true;
                    }
                    catch (IOException ex)
                    {
                        OnLockedFile(item);
                    }
                }
                else if (item.Status == ItemStatus.RenamedRemote)
                {

                    try
                    {
                        var newFilePath = Path.Combine(item.LocalFolder, item.NewNameAfterRename);
                        if (File.Exists(item.LocalFile))
                        {
                            File.Move(item.LocalFile, newFilePath);
                            item.LastModified = File.GetLastWriteTimeUtc(newFilePath);
                        }
                        if (File.Exists(item.LocalFile + ".spsync"))
                        {
                            File.Move(item.LocalFile + ".spsync", newFilePath + ".spsync");
                            item.LastModified = File.GetLastWriteTimeUtc(newFilePath + ".spsync");
                        }

                        item.Name = item.NewNameAfterRename;
                        item.Status = ItemStatus.Unchanged;
                    }
                    catch (IOException ex)
                    {
                        OnLockedFile(item);
                    }
                }
                else if (item.Status == ItemStatus.RenamedLocal)
                {

                    try
                    {
                        _sharePointManager.RenameItem(item.SharePointId, item.NewNameAfterRename);

                        item.Name = item.NewNameAfterRename;
                        item.Status = ItemStatus.Unchanged;
                    }
                    catch (IOException ex)
                    {
                        OnLockedFile(item);
                    }
                }
                else if (item.Status == ItemStatus.Conflict)
                {
                    // to keep both files, rename the local one
                    var newFileCopy = Path.Combine(item.LocalFolder,
                        Path.GetFileNameWithoutExtension(item.LocalFile) + "_" + Environment.MachineName + "_" +
                        DateTime.Now.Ticks.ToString() + Path.GetExtension(item.LocalFile));
                    File.Copy(item.LocalFile, newFileCopy);
                    File.SetLastWriteTimeUtc(item.LocalFile, item.LastModified.AddHours(-1));
                    item.Status = ItemStatus.Unchanged;
                    item.ETag = 0;
                }
            }
            catch (Exception ex)
            {
                OnErrorProcessing(item, ex);
            }
            return false;
        }

        private static void OnErrorProcessing(MetadataItem item, Exception ex)
        {
            item.HasError = true;
            item.LastError = ex.Message;
            var soapEx = ex as System.Web.Services.Protocols.SoapException;
            if (soapEx != null)
            {
                var detail = soapEx.Detail != null ? soapEx.Detail.OuterXml : "n/a";
                item.LastError = item.LastError + detail;
            }
        }

        private static void OnLockedFile(MetadataItem item)
        {
            //item.LastModified = item.LastModified - new TimeSpan(365, 0, 0, 0);
            //item.Status = ItemStatus.Unchanged;
        }

        private void ProcessFolder(MetadataItem item, bool syncToRemote, bool syncToLocal, out bool itemToDelete)
        {
            itemToDelete = false;

            var relFolder = item.LocalFolder.Replace(_localFolder, string.Empty).TrimStart('.', '\\');
            if (item.Status == ItemStatus.UpdatedLocal && syncToRemote)
            {
                try
                {
                    item.SharePointId = _sharePointManager.CreateFolder(relFolder, item.Name);
                    item.LastModified = Directory.GetLastWriteTimeUtc(item.LocalFile);
                    item.Status = ItemStatus.Unchanged;
                }
                catch (Exception ex)
                {
                }
            }
            else if (item.Status == ItemStatus.UpdatedRemote && syncToLocal)
            {

                if (!Directory.Exists(item.LocalFile))
                    Directory.CreateDirectory(item.LocalFile);

                item.LastModified = Directory.GetLastWriteTimeUtc(item.LocalFile);
                item.Status = ItemStatus.Unchanged;
            }
            else if (item.Status == ItemStatus.DeletedLocal && syncToRemote)
            {

                try
                {
                    _sharePointManager.DeleteFolder(relFolder, item.Name);
                }
                catch (Exception ex)
                {
                }
                itemToDelete = true;
            }
            else if (item.Status == ItemStatus.DeletedRemote && syncToLocal)
            {

                if (Directory.Exists(item.LocalFile))
                    FileHelper.RecycleDirectory(item.LocalFile);

                itemToDelete = true;
            }
            else if (item.Status == ItemStatus.RenamedRemote)
            {

                try
                {
                    var newFilePath = Path.Combine(item.LocalFolder, item.NewNameAfterRename);
                    if (Directory.Exists(item.LocalFile))
                    {
                        Directory.Move(item.LocalFile, newFilePath);
                        item.LastModified = Directory.GetLastWriteTimeUtc(newFilePath);
                    }

                    item.Name = item.NewNameAfterRename;
                    item.Status = ItemStatus.Unchanged;
                }
                catch (IOException ex)
                {
                    OnLockedFile(item);
                }
            }
            else if (item.Status == ItemStatus.RenamedLocal)
            {

                try
                {
                    _sharePointManager.RenameItem(item.SharePointId, item.NewNameAfterRename);

                    item.Name = item.NewNameAfterRename;
                    item.Status = ItemStatus.Unchanged;
                }
                catch (IOException ex)
                {
                    OnLockedFile(item);
                }
            }
            else if (item.Status == ItemStatus.Conflict)
            {
                
            }
        }

        private void CreateFoldersIfNotExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            Directory.CreateDirectory(path);
        }
    }
}
