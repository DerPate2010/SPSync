using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity.Infrastructure;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SPSync.Core
{
    partial class MetadataStoreEntities
    {
        public MetadataStoreEntities(string nameOrConnectionString): base(nameOrConnectionString)
        {
            
        }
        public MetadataStoreEntities(DbConnection connection): base(connection,true)
        {
            
        }
    }
}
