using System;
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
using System.Text.RegularExpressions;
using PInvoke;
using UsnJournal;

namespace SPSync.Core
{
    public class UsnEntry
    {
        public ulong FileRefNumber { get; set; }
        public string Path { get; set; }
        public string NewPath { get; set; }
        public uint ChangeType { get; set; }
    }

    public class SyncManager
    {
        private string _localFolder;
        private string _originalFolder;
        private SyncConfiguration _configuration;
        private SharePointManager _sharePointManager;
        private MetadataStore _metadataStore;
        private CancellationTokenSource _syncCancellation;
        private AutoResetEvent _syncEvent;
        private AutoResetEvent _changeEvent;
        private Task _syncTask;
        private CancellationTokenSource _metadataCancellation;
        private Task _metadataTask;
        private CancellationTokenSource _watchCancellation;
        private Task _watchTask;
        private bool _running;

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

        public void DownloadFile(string syncFileName)
        {
            string fullSyncFile = Path.Combine(_originalFolder, syncFileName);
            string origFilename = Path.GetFileNameWithoutExtension(syncFileName);
            _sharePointManager.DownloadFile(origFilename, _originalFolder, File.GetLastWriteTimeUtc(fullSyncFile));
            File.Delete(fullSyncFile);
        }

        public void WatchChanges()
        {
            _changeEvent= new AutoResetEvent(false);
            _watchCancellation = new CancellationTokenSource();
            _watchTask = Task.Run(WatchChangesInternal, _watchCancellation.Token);
        }

        private async Task WatchChangesInternal()
        {
            var volume = new DriveInfo(Path.GetPathRoot(_localFolder));
            var usnJournal = new NtfsUsnJournal(volume);

            Win32Api.USN_JOURNAL_DATA journalState = new Win32Api.USN_JOURNAL_DATA();
            NtfsUsnJournal.UsnJournalReturnCode rtn = usnJournal.GetUsnJournalState(ref journalState);
            if (_metadataStore.UpdateSequenceNumber == 0)
            {
                _metadataStore.UpdateSequenceNumber = journalState.NextUsn;
            }

            _sharePointManager.InitChangeTokenIfNecessary(_metadataStore);
            
            while (!_watchCancellation.IsCancellationRequested)
            {
                OnChangesProgress(0, ProgressStatus.Running, "Check for changes");

                try
                {
                    CheckUsnChanges(journalState, usnJournal);
                }
                catch (Exception e)
                {
                    Logger.Log("CheckUsnChanges failed: " + e.ToString());
                }

                try
                {
                    string newChangeToken;
                    var remoteChanges = _sharePointManager.GetChangedFiles(_metadataStore, (i, s) => { }, out newChangeToken);
                    var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;
                    Action<string> progress = (name) =>
                    {
                    };
                    ProcessRemoteChanges(_configuration.ConflictHandling, _watchCancellation.Token, remoteChanges, syncToLocal, Guid.NewGuid(), progress);
                    if (!string.IsNullOrEmpty(newChangeToken)) _metadataStore.ChangeToken = newChangeToken;
                    if (remoteChanges.Any()) _syncEvent.Set();
                }
                catch (Exception e)
                {
                    Logger.Log("GetChangedFiles failed: " + e.ToString());
                }

                OnChangesProgress(0, ProgressStatus.Idle, "Wait for changes");
                
                _changeEvent.WaitOne(TimeSpan.FromSeconds(30));
            }
            OnChangesProgress(0, ProgressStatus.Completed, "Stopped");
        }

        private void CheckUsnChanges(Win32Api.USN_JOURNAL_DATA journalState, NtfsUsnJournal usnJournal)
        {
            Guid correlationId = Guid.NewGuid();
            var  driveLetter= Path.GetPathRoot(_localFolder).TrimEnd('\\');
            uint reasonMask = Win32Api.USN_REASON_DATA_OVERWRITE |
                              Win32Api.USN_REASON_DATA_EXTEND |
                              Win32Api.USN_REASON_NAMED_DATA_OVERWRITE |
                              Win32Api.USN_REASON_NAMED_DATA_TRUNCATION |
                              Win32Api.USN_REASON_FILE_CREATE |
                              Win32Api.USN_REASON_FILE_DELETE |
                              Win32Api.USN_REASON_EA_CHANGE |
                              Win32Api.USN_REASON_SECURITY_CHANGE |
                              Win32Api.USN_REASON_RENAME_OLD_NAME |
                              Win32Api.USN_REASON_RENAME_NEW_NAME |
                              Win32Api.USN_REASON_INDEXABLE_CHANGE |
                              Win32Api.USN_REASON_BASIC_INFO_CHANGE |
                              Win32Api.USN_REASON_HARD_LINK_CHANGE |
                              Win32Api.USN_REASON_COMPRESSION_CHANGE |
                              Win32Api.USN_REASON_ENCRYPTION_CHANGE |
                              Win32Api.USN_REASON_OBJECT_ID_CHANGE |
                              Win32Api.USN_REASON_REPARSE_POINT_CHANGE |
                              Win32Api.USN_REASON_STREAM_CHANGE |
                              Win32Api.USN_REASON_CLOSE;

            Win32Api.USN_JOURNAL_DATA newUsnState;
            List<Win32Api.UsnEntry> usnEntries;

            var usnCurrentJournalState = new Win32Api.USN_JOURNAL_DATA()
            {
                UsnJournalID = journalState.UsnJournalID,
                NextUsn = _metadataStore.UpdateSequenceNumber,
            };

            NtfsUsnJournal.UsnJournalReturnCode rtnCode =
                usnJournal.GetUsnJournalEntries(usnCurrentJournalState, reasonMask, out usnEntries, out newUsnState);
            var changes = new List<UsnEntry>();

            foreach (var usnEntry in usnEntries)
            {
                try
                {
                    string path;
                    NtfsUsnJournal.UsnJournalReturnCode usnRtnCode =
                        usnJournal.GetPathFromFileReference(usnEntry.ParentFileReferenceNumber, out path);
                    if (usnRtnCode == NtfsUsnJournal.UsnJournalReturnCode.USN_JOURNAL_SUCCESS &&
                        0 != string.Compare(path, "Unavailable", true))
                    {
                        if (usnEntry.IsFile && !string.IsNullOrEmpty(usnEntry.Name))
                        {
                            string fullPath = driveLetter + Path.Combine(path, usnEntry.Name);
                            if (fullPath.ToLowerInvariant().StartsWith(_localFolder.ToLowerInvariant()) &&
                                !fullPath.Contains(".spsync"))
                            {
                                if (usnEntry.Reason == Win32Api.USN_REASON_RENAME_NEW_NAME)
                                {
                                    var rename =
                                        changes.FirstOrDefault(
                                            c => c.FileRefNumber == usnEntry.FileReferenceNumber &&
                                                 c.ChangeType == Win32Api.USN_REASON_RENAME_OLD_NAME);
                                    if (rename != null)
                                    {
                                        rename.NewPath = fullPath;
                                    }
                                }
                                else
                                {
                                    changes.Add(new UsnEntry()
                                    {
                                        Path = fullPath,
                                        ChangeType = usnEntry.Reason,
                                        FileRefNumber = usnEntry.FileReferenceNumber,
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Log("Process usn entry failed for " + usnEntry.Name + " " + usnEntry.Reason +
                               ": " + e.ToString());
                }
            }

            foreach (var change in changes)
            {
                try
                {


                    if ((change.ChangeType & Win32Api.USN_REASON_CLOSE) > 0)
                    {
                        continue;
                    }

                    var localFile = change.Path;
                    var item = _metadataStore.GetByFileName(localFile);

                    if (item == null)
                    {
                        if (change.ChangeType == Win32Api.USN_REASON_FILE_DELETE)
                        {

                        }
                        else
                        {
                            if (change.ChangeType == Win32Api.USN_REASON_RENAME_OLD_NAME)
                            {
                                localFile = change.NewPath;
                            }
                            _metadataStore.Add(new MetadataItem(localFile, ItemType.File));
                        }
                    }
                    else
                    {
                        if (change.ChangeType == Win32Api.USN_REASON_FILE_DELETE ||
                            change.ChangeType == Win32Api.USN_REASON_RENAME_OLD_NAME)
                        {
                            item.Status = ItemStatus.DeletedLocal;
                        }
                        else if (change.ChangeType == Win32Api.USN_REASON_RENAME_OLD_NAME)
                        {
                            item.NewNameAfterRename = Path.GetFileName(change.NewPath); //works for directories as well
                            item.Status = ItemStatus.RenamedLocal;

                            if (item.Type == ItemType.Folder)
                            {
                                foreach (var itemInFolder in _metadataStore.ItemsInDirSub(item.LocalFile))
                                {
                                    if (itemInFolder.Id == item.Id)
                                        continue;
                                    itemInFolder.LocalFolder =
                                        itemInFolder.LocalFolder.Replace(item.LocalFile, change.NewPath);
                                }
                            }
                        }
                        else
                        {
                            item.UpdateWithLocalInfo(_configuration.ConflictHandling, correlationId);
                        }

                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                }
                catch (Exception e)
                {
                    Logger.Log("Process grouped usn changes failed for " + change.Path + " " + change.ChangeType + ": " + e.ToString());
                }
            }
            if (changes.Any()) _syncEvent.Set();

            _metadataStore.UpdateSequenceNumber = newUsnState.NextUsn;
        }

        public void BuildMetadataStoreIfNecessary()
        {
            _metadataCancellation = new CancellationTokenSource();

            if (!_metadataStore.MetadataCompleted)
            {
                _metadataTask = Task.Run(() =>
                {
                    try
                    {
                        BuildMetadataStore(_configuration.ConflictHandling, _metadataCancellation.Token, true);
                    }
                    catch (AggregateException ae)
                    {
                        if (ae.InnerExceptions.All(e => e is OperationCanceledException))
                        {
                            OnMetadataProgress(100, ItemType.Unknown, ProgressStatus.Warning, "Cancled");
                        }
                        else
                        {
                            OnMetadataProgress(100, ItemType.Unknown, ProgressStatus.Error, ae.InnerException.Message, ae);
                        }
                    }
                    catch (OperationCanceledException e)
                    {
                        OnMetadataProgress(100, ItemType.Unknown, ProgressStatus.Warning, "Canceled");
                    }
                    catch (Exception e)
                    {
                        OnMetadataProgress(100, ItemType.Unknown, ProgressStatus.Error, e.Message, e);
                    }
                }, _metadataCancellation.Token);
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            BuildMetadataStoreIfNecessary();
            WatchChanges();
            RunSynchronization();
        }

        public async Task Stop()
        {
            if (!_running) return;
            _syncCancellation?.Cancel();
            _metadataCancellation?.Cancel();
            _watchCancellation?.Cancel();
            try
            {
                await Task.WhenAll(_syncTask, _metadataTask, _watchTask);
            }
            catch (Exception e)
            {
            }
            _running = false;
        }

        public void RunSynchronization()
        {
            _syncEvent = new AutoResetEvent(false);
            _syncCancellation = new CancellationTokenSource();
            _syncTask  = Task.Run(()=>SyncInternal(_syncCancellation.Token));
        }

        private void SyncInternal(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                RunSynchronizationInternal(cancellation);
                _syncEvent.WaitOne();
            }
        }

        public void RunSynchronizationInternal(CancellationToken cancellation)
        {
            try
            {
                OnSyncProgress(0, ProgressStatus.Analyzing);

                var countChanged = _metadataStore.GetItemsToProcess();
                SyncChanges(countChanged, cancellation);

                OnSyncProgress(100, ProgressStatus.Completed);
            }
            catch (OperationCanceledException ex)
            {
                OnSyncProgress(100, ProgressStatus.Completed);
            }
            catch (Exception ex)
            {
                //todo:
                if (_configuration.AuthenticationType == AuthenticationType.ADFS) //&& ex is webexception 403
                {
                    Adfs.AdfsHelper.InValidateCookie();
                }
                Logger.Log("An error has occured: " + ex.ToString());
                OnSyncProgress(100, ProgressStatus.Error, "An error has occured: " + ex.Message, ex);
            }
        }

        private int Synchronize(bool reviewOnly = false, bool rescanLocalFiles = true)
        {
            var countChanged = 1;

            lock (this)
            {
                OnSyncProgress(0, ProgressStatus.Analyzing);

                try
                {
                    BuildMetadataStore(_configuration.ConflictHandling, CancellationToken.None, rescanLocalFiles);
                    countChanged = _metadataStore.GetItemsToProcess();

                    if (reviewOnly || countChanged < 1)
                    {
                        OnSyncProgress(100, ProgressStatus.Completed);
                        return countChanged;
                    }

                    SyncChanges(countChanged, CancellationToken.None);

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
            _changeEvent.Set();
        }

        private void BuildMetadataStore(ConflictHandling conflictHandling, CancellationToken cancellation, bool rescanLocalFiles = true)
        {
            var correlationId = Guid.NewGuid();

            //reset item status for all items except the ones with errors
            //_metadataStore.ResetExceptErrors();

            var syncToRemote = (_configuration.Direction == SyncDirection.LocalToRemote || _configuration.Direction == SyncDirection.Both);
            var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;

            var searchOption = SearchOption.AllDirectories;
            int counter = 0;

            if (rescanLocalFiles)
            {
                OnMetadataProgress(0,ItemType.Unknown, ProgressStatus.Analyzing, "Processing local changes");

                #region Iterate local files/folders

                Parallel.ForEach(Directory.EnumerateDirectories(_localFolder, "*", searchOption),
                    new ParallelOptions() { MaxDegreeOfParallelism = 4 }
                    , localFolder =>
                {
                    cancellation.ThrowIfCancellationRequested();

                    OnMetadataProgress(0, ItemType.Folder, ProgressStatus.Analyzing, "Process " + Path.GetFileName(localFolder) + " " + counter++);

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
                        //item.UpdateWithLocalInfo(conflictHandling, correlationId);
                        //if (item.Status == ItemStatus.Conflict)
                        //    item.Status = OnItemConflict(item);
                    }

                });

                // update store for local files
                Parallel.ForEach(Directory.EnumerateFiles(_localFolder, "*.*", searchOption), 
                    new ParallelOptions() {MaxDegreeOfParallelism = 4}, 
                    localFile =>
                //foreach (var localFile in Directory.EnumerateFiles(_localFolder, "*.*", searchOption))
                {
                    cancellation.ThrowIfCancellationRequested();

                    OnMetadataProgress(0, ItemType.Folder, ProgressStatus.Analyzing, "Process " + Path.GetFileName(localFile) + " " + counter++);

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
                        //item.UpdateWithLocalInfo(conflictHandling, correlationId);
                        //if (item.Status == ItemStatus.Conflict)
                        //    item.Status = OnItemConflict(item);
                    }
                });

                #endregion
            }

            #region Iterate remote files/folders

            var remoteFileList = _sharePointManager.DownloadFileList();

            // update store for remote files/folders
            Action<string> progress = (name) =>
            {
                OnMetadataProgress(0, ItemType.File, ProgressStatus.Analyzing, "Process remote" + Path.GetFileName(name) + " " + counter++);
            };
            ProcessRemoteChanges(conflictHandling, cancellation, remoteFileList, syncToLocal, correlationId, progress);

            #endregion

            // reset error flag
            //_metadataStore.Items.Where(p => p.HasError && p.Status != ItemStatus.Unchanged).ToList().ForEach(p => p.HasError = false);

            _metadataStore.MetadataCompleted = true;

            _metadataStore.Save();

            OnMetadataProgress(100, ItemType.Unknown, ProgressStatus.Completed);
        }

        private void ProcessRemoteChanges(ConflictHandling conflictHandling, CancellationToken cancellation,
            List<SharePointItem> remoteFileList, bool syncToLocal, Guid correlationId, Action<string> progress)
        {
            foreach (var remoteItem in remoteFileList)
            {
                if (cancellation.IsCancellationRequested) break;

                if (!syncToLocal)
                    continue;

                var localFile = new DirectoryInfo(Path.Combine(_localFolder, remoteItem.FullFileName)).FullName;

                progress(localFile);

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
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name,
                            Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
                    }
                    // remote and local change
                    else
                    {
                        item.UpdateWithRemoteInfo(remoteItem.Id, remoteItem.ETag, remoteItem.LastModified, conflictHandling,
                            correlationId);
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
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name,
                            Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
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
                        _metadataStore.Add(new MetadataItem(remoteItem.Id, remoteItem.ETag, remoteItem.Name,
                            Path.GetDirectoryName(localFile), remoteItem.LastModified, remoteItem.Type));
                    }
                    else
                    {
                        item.UpdateWithRemoteInfo(remoteItem.Id, remoteItem.ETag, remoteItem.LastModified, conflictHandling,
                            correlationId);
                        if (item.Status == ItemStatus.Conflict)
                            item.Status = OnItemConflict(item);
                    }
                }
            }
        }

        private void SyncChanges(int countChanged, CancellationToken cancellation)
        {

            var syncToRemote = (_configuration.Direction == SyncDirection.LocalToRemote || _configuration.Direction == SyncDirection.Both);
            var syncToLocal = _configuration.Direction == SyncDirection.RemoteToLocal || _configuration.Direction == SyncDirection.Both;

            int countProcessed=0;
            MetadataItem item;
            while (!cancellation.IsCancellationRequested && _metadataStore.GetNextItemToProcess(out item))
            {
                countProcessed++;
                OnSyncProgress((int)(((double)countProcessed / (double)countChanged) * 100),
                    ProgressStatus.Running, string.Format("{1} {0}...", item.Name, GetLogMessage(item)));
                Logger.LogDebug("Sync item: " + GetLogMessage(item));
                try
                {

                    if (FitsOneOfMultipleMasks(item.Name, _metadataStore.IgnorePattern))
                    {
                        item.Status = item.Status + MetadataStore.POSTPONE_OFFSET;
                        continue;
                    }

                    bool itemToDelete = false;
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
                    else
                    {
                        if (item.Status != ItemStatus.Unchanged)
                        {
                            item.Status = item.Status + MetadataStore.POSTPONE_OFFSET;
                        }
                    }

                }
                catch (Exception e)
                {
                    Logger.Log("Error processing item " + item.Id + ": " + e);
                }
                finally
                {
                    try
                    {
                        if (item.Status != ItemStatus.Unchanged)
                        {
                            item.Status = item.Status + MetadataStore.POSTPONE_OFFSET;
                        }
                    }
                    catch (Exception e2)
                    {
                        Logger.Log("Cannot postpone item " + item.Id + ": " + e2);
                    }

                }
            }
            
            OnSyncProgress(90, ProgressStatus.Running, "Finalizing...");

            _metadataStore.ResetPostponed();

            _metadataStore.Save();
        }

        private bool FitsOneOfMultipleMasks(string fileName, string[] fileMasks)
        {
            return fileMasks.Any(fileMask => FitsMask(fileName, fileMask));
        }

        private bool FitsMask(string fileName, string fileMask)
        {
            string pattern =
                '^' +
                Regex.Escape(fileMask.Replace(".", "__DOT__")
                        .Replace("*", "__STAR__")
                        .Replace("?", "__QM__"))
                    .Replace("__DOT__", "[.]")
                    .Replace("__STAR__", ".*")
                    .Replace("__QM__", ".")
                + '$';
            return new Regex(pattern, RegexOptions.IgnoreCase).IsMatch(fileName);
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
