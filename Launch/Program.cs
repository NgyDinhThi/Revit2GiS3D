using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Launch
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var revitPath = @"D:\Autodesk\Revit\Revit\Revit 2024\Revit.exe";
                var process = Process.Start(revitPath);

                Console.WriteLine("Debug start");

                if (process != null)
                {
                    process.WaitForExit(); 
                }

                Console.WriteLine("Debug end.");
            }
            catch (Exception ex)
            {   
                Console.WriteLine("Lỗi khi mở Revit: " + ex.Message);
            }
        }
    }
}
