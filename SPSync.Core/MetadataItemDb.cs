//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated from a template.
//
//     Manual changes to this file may cause unexpected behavior in your application.
//     Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace SPSync.Core
{
    using System;
    using System.Collections.Generic;
    
    public partial class MetadataItemDb
    {
        public string Id { get; set; }
        public Nullable<long> SharePointId { get; set; }
        public string LocalFolder { get; set; }
        public string Name { get; set; }
        public Nullable<long> LastModified { get; set; }
        public Nullable<long> ETag { get; set; }
        public Nullable<long> Status { get; set; }
        public Nullable<long> HasError { get; set; }
        public string LastError { get; set; }
        public Nullable<long> Type { get; set; }
    }
}