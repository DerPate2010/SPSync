﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using SPSync.Core.Common;
using SQLiteWithCSharp.Utility;

namespace SPSync.Core.Metadata
{

    public class MetadataItem
    {
        private readonly MetadataItemDb _itemDb;
        private readonly MetadataStore _metadataStore;
        private ItemStatus _status;
        private bool _isLoading;
        public MetadataItem() { }

        internal MetadataItem(string localPath, ItemType type)
        {
            _isLoading = true;
            Id = Guid.NewGuid();
            SharePointId = -1;
            ETag = -1;
            LocalFolder = Path.GetDirectoryName(localPath);

            if (type == ItemType.File)
            {
                Name = Path.GetFileName(localPath);
                LastModified = File.GetLastWriteTimeUtc(localPath);
            }
            if (type == ItemType.Folder)
            {
                var pathDi = new DirectoryInfo(localPath);
                Name = pathDi.Name;
                LastModified = pathDi.LastWriteTimeUtc;
            }

            Status = ItemStatus.UpdatedLocal;
            Type = type;
            HasError = false;

            Logger.LogDebug(Guid.NewGuid(), Id, "Created metadata item from local file. Name={0}, LocalFolder={1}, Type={2}", Name, LocalFolder, Type);
            _isLoading = false;
        }

        internal MetadataItem(int sharePointId, int etag, string name, string localFolder, DateTime lastModified, ItemType type)
        {
            _isLoading = true;
            Id = Guid.NewGuid();
            SharePointId = sharePointId;
            ETag = etag;
            LocalFolder = localFolder;
            Name = name;
            LastModified = lastModified;
            Status = ItemStatus.UpdatedRemote;
            Type = type;
            HasError = false;

            Logger.LogDebug(Guid.NewGuid(), Id, "Created metadata item from remote file. SharePointId={0}, Name={1}, LocalFolder={2}, Type={3}, ETag={4}", SharePointId, Name, LocalFolder, Type, ETag);
            _isLoading = false;
        }

        public MetadataItem(MetadataItemDb itemDb, MetadataStore metadataStore)
        {
            _isLoading = true;
            _itemDb = itemDb;
            _metadataStore = metadataStore;
            FromDb(itemDb);
            _isLoading = false;
        }

        public Guid Id { get; set; }

        public int SharePointId { get; set; }

        public string LocalFolder { get; set; }

        public string LocalFile => Path.Combine(LocalFolder, Name);

        public string Name { get; set; }

        public DateTime LastModified { get; set; }

        public int ETag { get; set; }

        public ItemStatus Status
        {
            get { return _status; }
            set { _status = value;
                OnPropertyChanged();
            }
        }

        private void OnPropertyChanged()
        {
            if (_isLoading) return;
            if (_itemDb != null)
            {
                ToDb(_itemDb);
                _metadataStore.SaveChanges();
            }
            else
            {
                _metadataStore.Update(this);
            }
            
        }

        public bool HasError { get; set; }
        public string LastError { get; set; }

        public ItemType Type { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        public ConflictData ConflictData { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        public string NewNameAfterRename { get; set; }

        internal void UpdateWithLocalInfo(ConflictHandling conflictHandling, Guid correlationId)
        {
            Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Name={0}, LocalFolder={1}, Type={2}", Name, LocalFolder, Type);
            if (Type == ItemType.File)
            {
                var mod = File.GetLastWriteTimeUtc(Path.Combine(LocalFolder, Name));
                if (mod == LastModified)
                {
                    // means something went wrong on initial sync, so reset status to updatedlocal
                    if (SharePointId == -1)
                    {
                        Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Possibly error: LastWriteTime=LastModified but SharePointId=-1");
                        Status = ItemStatus.UpdatedLocal;
                    }
                    else
                    {
                        Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Nothing to do");
                    }

                    return;
                }

                if (mod > LastModified)
                {
                    Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) File is greater than metadata LastModified. Status={0}", Status);
                    if (Status == ItemStatus.UpdatedRemote || Status == ItemStatus.Conflict)
                    {
                        switch (conflictHandling)
                        {
                            case ConflictHandling.ManualConflictHandling:
                                ConflictData = new ConflictData()
                                {
                                    LocalLastModified = mod,
                                    RemoteLastModified = LastModified
                                };
                                Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Set status to 'Conflict'");
                                Status = ItemStatus.Conflict;
                                return;
                            case ConflictHandling.OverwriteLocalChanges:
                                Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Set status to 'UpdatedRemote'");
                                Status = ItemStatus.UpdatedRemote;
                                return;
                            case ConflictHandling.OverwriteRemoteChanges:
                                break;
                        }
                    }

                    Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) Set status to 'UpdatedLocal' and LastModified to {0}", mod);
                    LastModified = mod;
                    Status = ItemStatus.UpdatedLocal;
                }
                else
                {
                    Logger.LogDebug(correlationId, Id, "(UpdateWithLocalInfo) File is older than metadata LastModified. Status set to 'UpdatedRemote'");
                    Status = ItemStatus.UpdatedRemote;
                }
            }
            OnPropertyChanged();
        }

        internal void UpdateWithRemoteInfo(int sharePointId, int etag, DateTime lastModified, ConflictHandling conflictHandling, Guid correlationId)
        {
            Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) SharePointId={0}, ETag={1}, Name={2}, LocalFolder={3}, Type={4}, CurrentStatus={5}", sharePointId, etag, Name, LocalFolder, Type, Status);

            if (Type == ItemType.File)
            {
                Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) SharePointId old={0}, SharePointId new={1}", SharePointId, sharePointId);
                SharePointId = sharePointId;

                if (ETag == -1)
                {
                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Current ETag=-1, new ETag={0}", etag);
                    ETag = etag;
                    if (lastModified > LastModified)
                    {
                        Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) RemoteLastModified is greater than current LastModified. Status={0}", Status);
                        if (Status == ItemStatus.UpdatedLocal || Status == ItemStatus.Conflict)
                        {
                            switch (conflictHandling)
                            {
                                case ConflictHandling.ManualConflictHandling:
                                    ConflictData = new ConflictData()
                                    {
                                        LocalLastModified = LastModified,
                                        RemoteLastModified = lastModified
                                    };
                                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'Conflict'");
                                    Status = ItemStatus.Conflict;
                                    return;
                                case ConflictHandling.OverwriteRemoteChanges:
                                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'UpdatedLocal'");
                                    Status = ItemStatus.UpdatedLocal;
                                    return;
                                case ConflictHandling.OverwriteLocalChanges:
                                    break;
                            }
                        }

                        Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'UpdatedRemote' and LastModified to {0}", lastModified);
                        LastModified = lastModified;
                        Status = ItemStatus.UpdatedRemote;
                        return;
                    }
                    if (lastModified < LastModified)
                    {
                        Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) RemoteLastModified is less than current LasModified. Set status to 'UpdatedLocal'");
                        Status = ItemStatus.UpdatedLocal;
                        return;
                    }
                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) LastModified dates are equal");
                    Status = ItemStatus.Unchanged;
                    return;
                }

                if (File.Exists(Path.Combine(LocalFolder, Name) + ".spsync"))
                {
                    Status = ItemStatus.Unchanged;
                    return;
                }

                if (ETag < etag)
                {
                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Current ETag is less than remote etag. Status={0}", Status);
                    if (Status == ItemStatus.UpdatedLocal || Status == ItemStatus.Conflict)
                    {
                        switch (conflictHandling)
                        {
                            case ConflictHandling.ManualConflictHandling:
                                ConflictData = new ConflictData()
                                {
                                    LocalLastModified = LastModified,
                                    RemoteLastModified = lastModified
                                };
                                Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'Conflict'");
                                Status = ItemStatus.Conflict;
                                return;
                            case ConflictHandling.OverwriteRemoteChanges:
                                Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'UpdatedLocal'");
                                Status = ItemStatus.UpdatedLocal;
                                return;
                            case ConflictHandling.OverwriteLocalChanges:
                                break;
                        }
                    }

                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Set status to 'UpdatedRemote' and ETag to {0}", etag);
                    ETag = etag;
                    Status = ItemStatus.UpdatedRemote;
                    return;
                }
                if (ETag > etag)
                {
                    Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) Current ETag is greater than remote etag. Set Status to 'UpdatedLocal'");
                    Status = ItemStatus.UpdatedLocal;
                    return;
                }
                Logger.LogDebug(correlationId, Id, "(UpdateWithRemoteInfo) ETags are equal.");
            }
            OnPropertyChanged();
        }

        internal MetadataItem DeepClone()
        {
            using (var ms = new MemoryStream())
            {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                formatter.Serialize(ms, this);
                ms.Position = 0;

                return (MetadataItem)formatter.Deserialize(ms);
            }
        }

        internal void ToDb(MetadataItemDb item)
        {
            if (item.Id == null)
            {
                item.Id = Id.ToString();
            }
            else
            {
                if (item.Id != Id.ToString()) throw new ArgumentException();
            }
            item.LocalFolder = LocalFolder;
            item.Name = Name;
            item.SharePointId = SharePointId;
            item.ETag = ETag;
            item.HasError = HasError ? 1 : 0;
            item.LastError = LastError;
            item.LastModified = LastModified.ToBinary();
            item.Status = (long)Status;
            item.Type = (long)Type;
        }
        internal void FromDb(MetadataItemDb item)
        {
            _isLoading = true;
            Id = Guid.Parse(item.Id);
            LocalFolder = item.LocalFolder;
            Name = item.Name;
            SharePointId = (int) item.SharePointId.GetValueOrDefault();
            ETag = (int)item.ETag.GetValueOrDefault();
            HasError = item.HasError.GetValueOrDefault()==1;
            LastError = item.LastError;
            LastModified = DateTime.FromBinary(item.LastModified.GetValueOrDefault());
            Status = (ItemStatus) item.Status.GetValueOrDefault();
            Type = (ItemType) item.Type.GetValueOrDefault();
            _isLoading = false;
        }
    }
}
