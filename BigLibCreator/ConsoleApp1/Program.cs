using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    class Program
    {
        private static long counter;

        static void Main(string[] args)
        {
            var dir = new DirectoryInfo(".");
            FillDir(dir, 0);
        }

        private static void FillDir(DirectoryInfo dir, int level)
        {
            for (int i = 0; i < 10; i++)
            {
                var file = new FileInfo(Path.Combine(dir.FullName, "File " + counter++));
                file.Create().Close();
                //Console.WriteLine(file.FullName);
            }
            if (level==4) return;
            for (int i = 0; i < 10; i++)
            {
                var subDir = dir.CreateSubdirectory("Folder " + i);
                FillDir(subDir, level+1);
            }
        }
    }
}
