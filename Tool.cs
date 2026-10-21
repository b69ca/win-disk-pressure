using System.Diagnostics;
using System.Text.Json.Nodes;
namespace WinDiagnostics;
public static class Tool
{
    public const string Name="win-disk-pressure";
    public static readonly string[] ValueOptions=["--path","--top","--max-entries","--max-seconds"];
    public static readonly string[] Flags=[];
    public const string Help="win-disk-pressure [--path C:\\] [--top 20] [--max-entries 200000] [--max-seconds 60] [--json]\nCounts logical file lengths, skips reparse points, and reports the largest observed files and top-level directories. Never deletes files.";
    public static void ValidateOptions(Options o){if(o.Get("--path",Environment.GetEnvironmentVariable("SystemDrive")+"\\").Any(char.IsControl))throw new ArgumentException("Invalid path.");o.Number("--top",20,1,100);o.Number("--max-entries",200000,1,2000000);o.Number("--max-seconds",60,1,600);}
    public static Task<Collection> Collect(Options o,CancellationToken ct)=>Task.Run(()=>Scan(o.Get("--path",(Environment.GetEnvironmentVariable("SystemDrive")??"C:")+"\\"),o.Number("--top",20,1,100),o.Number("--max-entries",200000,1,2000000),o.Number("--max-seconds",60,1,600),ct),ct);
    public static Collection Scan(string path,int top,int maxEntries,int maxSeconds,CancellationToken ct)
    {
        if(top<1||top>100||maxEntries<1||maxSeconds<1)throw new ArgumentException("Invalid scan limits.");
        path=Path.GetFullPath(path);
        var rootAttributes=File.GetAttributes(path);
        if(!rootAttributes.HasFlag(FileAttributes.Directory))throw new ArgumentException("--path must identify a directory.");
        if(rootAttributes.HasFlag(FileAttributes.ReparsePoint))throw new ArgumentException("The scan root must not be a reparse point. Specify the real directory explicitly.");
        var issues=new JsonArray();var stack=new Stack<(string Path,string Group)>();stack.Push((path,"[root files]"));
        var groups=new Dictionary<string,long>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        var known=new Dictionary<string,long>(StringComparer.Ordinal);
        var largest=new PriorityQueue<(string Path,long Bytes),long>();long bytes=0,files=0;int entries=0,skipped=0,errors=0;bool capped=false;
        var watch=Stopwatch.StartNew();
        void Issue(string area,Exception e){errors++;if(issues.Count<20)issues.Add(new JsonObject{["area"]=area,["message"]=e.Message});}
        while(stack.Count>0)
        {
            ct.ThrowIfCancellationRequested();
            if(entries>=maxEntries||watch.Elapsed.TotalSeconds>=maxSeconds){capped=true;break;}
            var current=stack.Pop();
            try
            {
                using var iterator=Directory.EnumerateFileSystemEntries(current.Path).GetEnumerator();
                while(true)
                {
                    ct.ThrowIfCancellationRequested();
                    if(entries>=maxEntries||watch.Elapsed.TotalSeconds>=maxSeconds){capped=true;break;}
                    bool hasNext;try{hasNext=iterator.MoveNext();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Issue(current.Path,e);break;}
                    if(!hasNext)break;var entry=iterator.Current;entries++;
                    try
                    {
                        var attributes=File.GetAttributes(entry);
                        if(attributes.HasFlag(FileAttributes.ReparsePoint)){skipped++;continue;}
                        if(attributes.HasFlag(FileAttributes.Directory))
                        {
                            var group=current.Path==path?Path.GetFileName(entry):current.Group;
                            stack.Push((entry,group));continue;
                        }
                        var length=new FileInfo(entry).Length;bytes+=length;files++;
                        groups[current.Group]=groups.GetValueOrDefault(current.Group)+length;
                        var location=ClassifyLocation(entry);if(location.Length>0)known[location]=known.GetValueOrDefault(location)+length;
                        largest.Enqueue((entry,length),length);if(largest.Count>top)largest.Dequeue();
                    }
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException){Issue(entry,e);}
                }
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){Issue(current.Path,e);}
            if(capped)break;
        }
        long? free=null,total=null;
        try{var drive=new DriveInfo(Path.GetPathRoot(path)!);free=drive.AvailableFreeSpace;total=drive.TotalSize;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){Issue("drive",e);}
        var fileRows=new JsonArray(largest.UnorderedItems.OrderByDescending(x=>x.Element.Bytes).Select(x=>(JsonNode?)new JsonObject{["path"]=x.Element.Path,["bytes"]=x.Element.Bytes}).ToArray());
        var groupRows=new JsonArray(groups.OrderByDescending(x=>x.Value).Take(top).Select(x=>(JsonNode?)new JsonObject{["name"]=x.Key,["bytes"]=x.Value}).ToArray());
        return new(new JsonObject{["path"]=path,["filesObserved"]=files,["entriesVisited"]=entries,["logicalBytesObserved"]=bytes,["availableFreeBytes"]=free,["volumeTotalBytes"]=total,["reparsePointsSkipped"]=skipped,["accessOrIoErrors"]=errors,["budgetReached"]=capped,["elapsedSeconds"]=watch.Elapsed.TotalSeconds,["knownLocations"]=new JsonArray(known.OrderByDescending(x=>x.Value).Select(x=>(JsonNode?)new JsonObject{["location"]=x.Key,["observedLogicalBytes"]=x.Value}).ToArray()),["largestFiles"]=fileRows,["topLevelGroups"]=groupRows},issues);
    }
    public static string ClassifyLocation(string path)
    {
        var p=path.Replace('/','\\').ToUpperInvariant();
        if(p.Contains(@"\WINDOWS\SOFTWAREDISTRIBUTION\DOWNLOAD\"))return "Windows Update download staging (managed)";
        if(p.Contains(@"\WINDOWS\WINSXS\"))return "Windows component store (managed; not a disposable cache)";
        if(p.Contains(@"\WINDOWS\INSTALLER\"))return "Windows Installer cache (managed)";
        if(p.Contains(@"\WINDOWS\TEMP\"))return "Windows temporary directory";
        if(p.Contains(@"\APPDATA\LOCAL\TEMP\"))return "User temporary directories";
        if(p.Contains(@"\APPDATA\LOCAL\MICROSOFT\WINDOWS\INETCACHE\"))return "User Internet cache";
        if((p.Contains(@"\GOOGLE\CHROME\USER DATA\")||p.Contains(@"\MICROSOFT\EDGE\USER DATA\"))&&(p.Contains(@"\CACHE\")||p.Contains(@"\CODE CACHE\")))return "Chromium browser cache";
        if(p.Contains(@"\DELIVERYOPTIMIZATION\CACHE\"))return "Delivery Optimization cache (managed)";
        return "";
    }
    public static void ValidateEvidence(JsonObject e){J.Require(e,"path","filesObserved","logicalBytesObserved","budgetReached","largestFiles","topLevelGroups");J.RequireRows(e,"largestFiles");J.RequireRows(e,"topLevelGroups");if(J.Number(e,"logicalBytesObserved",-1)<0)throw new ArgumentException("Logical byte count must be nonnegative.");}
    public static List<Finding> Analyze(JsonObject e)
    {
        var r=new List<Finding>();
        if(J.Bool(e,"budgetReached"))r.Add(new("scan-budget-reached","warning","Scan stopped at its budget","Sizes and rankings cover only the observed subset; they are not totals for the entire directory.","Narrow --path or deliberately increase scan limits."));
        if(J.Number(e,"accessOrIoErrors")>0)r.Add(new("scan-access-gaps","warning","Some entries could not be inspected",$"{J.Text(e,"accessOrIoErrors")} access/I/O errors; the first 20 are included. Rankings can omit large inaccessible files.","Review the affected paths and rerun only where the required access is appropriate."));
        if(J.Number(e,"reparsePointsSkipped")>0)r.Add(new("reparse-points-skipped","info","Links and reparse points were skipped","This avoids following junctions, symbolic links, and cloud placeholders. Their data is not counted.","Scan a real target directory explicitly if it is relevant."));
        var free=J.Number(e,"availableFreeBytes",double.NaN);var total=J.Number(e,"volumeTotalBytes",double.NaN);
        if(double.IsFinite(free)&&(free<5*1073741824d||total>0&&free/total<0.1))r.Add(new("disk-pressure","warning","The volume has low available space",$"Available: {J.Bytes(free)}. Rule: less than 5 GiB or 10% of the volume.","Review the largest observed files and ownership before moving or deleting anything."));
        r.Add(new("logical-size-limit","info","File lengths are logical sizes","Hard links may be counted more than once; sparse/compressed files, filesystem metadata, snapshots, and reserved storage differ from logical lengths. Free-space figures apply to the whole volume, even for a subdirectory scan.","Use filesystem-specific tools when reconciling logical totals against used volume space."));return r;
    }
    public static IEnumerable<string> Describe(JsonObject e){yield return $"Scan root: {J.Text(e,"path")}";yield return $"Observed: {J.Text(e,"filesObserved")} files | {J.Bytes(J.Number(e,"logicalBytesObserved"))}";yield return $"Volume free: {(e["availableFreeBytes"] is null?"unavailable":J.Bytes(J.Number(e,"availableFreeBytes")))}";foreach(var row in J.Rows(e,"topLevelGroups"))yield return $"Group {J.Text(row,"name")}: {J.Bytes(J.Number(row,"bytes"))}";foreach(var row in J.Rows(e,"knownLocations"))yield return $"Known location {J.Text(row,"location")}: {J.Bytes(J.Number(row,"observedLogicalBytes"))} observed";foreach(var row in J.Rows(e,"largestFiles"))yield return $"File {J.Bytes(J.Number(row,"bytes"))}: {J.Text(row,"path")}";}
    public static string[] SmokeArgs=>["--path",Path.GetTempPath(),"--max-entries","50","--max-seconds","2","--top","3"];
}
