using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml.Serialization;
using SPSync.Core.Common;
using SQLiteWithCSharp.Utility;

namespace SPSync.Core.Metadata
{ 

    public class MetadataStore
    {
        public const string STOREFOLDER = ".spsync";
        private const string CHANGE_TOKEN_FILE = "ChangeToken.dat";
        private const string USN_FILE = "UpdateSequenceNumber.dat";

        public string ChangeToken
        {
            get { return _changeToken; }
            set
            {
                if (_changeToken != value)
                {
                    _changeToken = value;
                    Save();
                }
            }
        }

        public long UpdateSequenceNumber
        {
            get { return _updateSequenceNumber; }
            set
            {
                if (_updateSequenceNumber != value)
                {
                    _updateSequenceNumber = value;
                    Save();
                }
            }
        }

        public MetadataStoreEntities Db
        {
            get { return _db; }
        }

        private string localFolder;
        private MetadataStoreEntities _db;

        public MetadataStore(string localFolder)
        {
            this.localFolder = localFolder;
            Load();
        }

        private void Load()
        {
            var storeFolder = Path.Combine(localFolder, STOREFOLDER);
            EnsureStoreFolder(storeFolder);

            try
            {
                var dbFile = GetDbFilename(localFolder);
                if (!File.Exists(dbFile))
                {
                    var dbSource =Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location),"MetadataStore.db");
                    File.Copy(dbSource, dbFile, false);
                }
                _db = new MetadataStoreEntities($"metadata=res://*/MetadataStoreModel.csdl|res://*/MetadataStoreModel.ssdl|res://*/MetadataStoreModel.msl;provider=System.Data.SQLite.EF6;provider connection string=\"data source={dbFile}\"");
               
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading metadata store for {0} {1}{2}{3}", localFolder, ex.Message, Environment.NewLine, ex.StackTrace);
            }

            try
            {
                _changeToken = File.ReadAllText(Path.Combine(storeFolder, CHANGE_TOKEN_FILE));
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading ChangeToken for {0} {1}", localFolder, ex.Message);
            }
            try
            {
                var usnString = File.ReadAllText(Path.Combine(storeFolder, USN_FILE));
                long.TryParse(usnString, out _updateSequenceNumber);
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading UpdateSequenceNumber for {0} {1}", localFolder, ex.Message);
            }
        }

        private static string GetDbFilename(string localFolder)
        {
            var storeFolder = Path.Combine(localFolder, STOREFOLDER);
            var hash = Convert.ToBase64String(System.Security.Cryptography.SHA1.Create()
                .ComputeHash(Encoding.Default.GetBytes(localFolder))).Replace('/', '-').Replace('\\', '-');
            var dbFile = Path.Combine(storeFolder, "MetadataStore.db");
            return dbFile;
        }

        public void Save()
        {
            var storeFolder = Path.Combine(localFolder, STOREFOLDER);
            EnsureStoreFolder(storeFolder);

            try
            {
                Db.SaveChanges();
            }
            catch (Exception ex)
            {
                Logger.Log("Error saving metadata store for {0} {1}{2}{3}", localFolder, ex.Message, Environment.NewLine, ex.StackTrace);
            }

            try
            {
                File.WriteAllText(Path.Combine(storeFolder, CHANGE_TOKEN_FILE), ChangeToken);
            }
            catch { }
            try
            {
                File.WriteAllText(Path.Combine(storeFolder, USN_FILE), UpdateSequenceNumber.ToString());
            }
            catch { }
        }

        public MetadataItem GetById(Guid id)
        {
            var itemDb = Db.MetadataStore.Find(id.ToString());
            var item = ToMetadataItem(itemDb);
            return item;
        }

        public void Delete(Guid id)
        {
            var item = Db.MetadataStore.Find(id.ToString());
            if (item != null) Db.MetadataStore.Remove(item);
            _db.SaveChanges();
        }

        public void Update(MetadataItem item)
        {
            var itemDb = Db.MetadataStore.Find(item.Id.ToString());
            item.ToDb(itemDb);
            _db.SaveChanges();
        }

        public void Add(MetadataItem item)
        {
            lock (_lock)
            {
                var itemDb = new MetadataItemDb();
                item.ToDb(itemDb);
                Db.MetadataStore.Add(itemDb);
                _db.SaveChanges();
            }
        }

        private object _lock = new object();
        private string _changeToken;
        private long _updateSequenceNumber;

        public MetadataItem GetByFileName(string file)
        {
            lock (_lock)
            {
                var fileInfo = new FileInfo(file);

                var itemDb = Db.MetadataStore.FirstOrDefault(i => i.LocalFolder == fileInfo.DirectoryName && i.Name == fileInfo.Name);
                if (itemDb == null) return null;
                return ToMetadataItem(itemDb);
            }
        }

        public MetadataItem GetByItemId(int sharePointId)
        {
            var itemDb = Db.MetadataStore.FirstOrDefault(i => i.SharePointId == sharePointId);
            if (itemDb == null) return null;
            return ToMetadataItem(itemDb);
        }

        public MetadataItem[] GetResults()
        {
            List<MetadataItem> results = new List<MetadataItem>();

            //foreach (var item in Items)
            //{
            //    results.Add(item.DeepClone());
            //}

            return results.ToArray();
        }

        public static void DeleteStoreForFolder(string localFolder)
        {
            try
            {
                var storeFolder = Path.Combine(localFolder, STOREFOLDER);
                EnsureStoreFolder(storeFolder);

                var dbFile = GetDbFilename(localFolder);
                if (File.Exists(dbFile))
                    File.Delete(dbFile);
            }
            catch
            { }
        }

        public static void DeleteChangeTokenForFolder(string localFolder)
        {
            try
            {
                var storeFolder = Path.Combine(localFolder, STOREFOLDER);

                var ctFile = Path.Combine(storeFolder, CHANGE_TOKEN_FILE);
                if (File.Exists(ctFile))
                {
                    File.Delete(ctFile);
                }
            }
            catch
            { }
        }

        private static void EnsureStoreFolder(string storeFolder)
        {
            if (Directory.Exists(storeFolder))
                return;

            try
            {
                var folder = Directory.CreateDirectory(storeFolder);
                folder.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex)
            {
                Logger.Log("Storefolder {0} cannot be created: {1}", storeFolder, ex.Message);
                Logger.LogDebug("Storefolder cannot be created: {0}{1}{2}", ex.Message, Environment.NewLine, ex.StackTrace);
                throw new ApplicationException("Metadatastore folder (" + storeFolder + ") cannot be created. Make sure you have write access to the folder you want to sync.", ex);
            }
        }

        public IEnumerable<MetadataItem> ItemsInDirSub(string folder)
        {
            var items = _db.MetadataStore.Where(p => p.LocalFolder.Contains(folder));
            return BuildItemList(items);
        }

        private List<MetadataItem> BuildItemList(IQueryable<MetadataItemDb> items)
        {
            return items.ToList().Select(ToMetadataItem).ToList();
        }

        private MetadataItem ToMetadataItem(MetadataItemDb arg)
        {
            if (arg == null) return null;
            return new MetadataItem(arg, this);
        }

        public void ResetExceptErrors()
        {
            _db.MetadataStore.Where(p => p.Status != (long)ItemStatus.Conflict).ToList().ForEach(p => { p.Status = (long)ItemStatus.Unchanged; p.HasError = 0; });
            _db.SaveChanges();
        }

        internal IEnumerable<MetadataItem> ItemsUnchangedNoError(ItemType file)
        {
            var items = _db.MetadataStore.Where(p => p.Status == (long)ItemStatus.Unchanged && p.Type == (long)ItemType.File && p.HasError==0);
            return BuildItemList(items);
        }

        public IEnumerable<MetadataItem> ItemsInDir(string folder)
        {
            
            var items = _db.MetadataStore.Where(p => p.LocalFolder == folder);
            return BuildItemList(items);

        }

        public IEnumerable<MetadataItem> ItemsChanged()
        {
            var items = _db.MetadataStore.Where(p => p.Status != (long)ItemStatus.Unchanged);
            return BuildItemList(items);
        }

        //public IEnumerable<MetadataItem> ItemsChangedNoError(ItemType folder)
        //{
        //    var items = _db.MetadataStore.Where(p => p.Status != (long)ItemStatus.Unchanged && p.Type == (long)folder && p.HasError==0);
        //    return BuildItemList(items);
        //}

        public IEnumerable<MetadataItem> ItemsWithError()
        {
            var items = _db.MetadataStore.Where(p => p.HasError == 1);
            return BuildItemList(items);
        }

        public bool GetNextItemToProcess(out MetadataItem item)
        {
            var itemDb = _db.MetadataStore.FirstOrDefault(p => p.Status != (long)ItemStatus.Unchanged && p.HasError == 0);
            item = ToMetadataItem(itemDb);
            return item != null;
        }
        public int GetItemsToProcess()
        {
            return _db.MetadataStore.Count(p => p.Status != (long)ItemStatus.Unchanged && p.HasError == 0);
        }
    }
}
