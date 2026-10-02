using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Text.Json;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Views;
using DatesErp.PrintSmoke;
using PdfSharp.Pdf.IO;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value,string label) { if(!value)throw new InvalidOperationException(label);_checks++;Console.WriteLine("PASS "+label); }
    [STAThread]
    private static int Main(string[] args)
    {
        string folder=Path.GetFullPath(args.FirstOrDefault()??"print-smoke-evidence");Directory.CreateDirectory(folder);
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var results=new List<object>();
        try
        {
            foreach(var (id,model) in PrintSamples.Create())
            {
                var spec=PrintSchema.FromPhase(model);
                var layout=PrintLayout.Paginate(spec,160,(s,w,f,b)=>Math.Max(1,(int)Math.Ceiling(PrintRenderer.Text(s,w,f,b).Height/PrintLayout.LineHeight)));
                Check(layout.Count>1,id+" actual WPF font measurement paginates long data");
                var doc=PhasePrint.Build(model);var paginator=doc.DocumentPaginator;paginator.ComputePageCount();
                Check(paginator.PageCount>1,id+" has multiple fixed pages");
                var original=paginator.PageSize;int count=paginator.PageCount;
                for(int p=0;p<count;p++)Check(paginator.GetPage(p).Size==original,id+" page size "+p);
                // All text fixtures should fit their cells with WPF emergency wrapping, not ellipsizing.
                foreach(string value in new[]{"العربية 123 — ABC-DOC-2026",new string('A',180),"اسم طويل مع ملاحظات وأرقام ١٢٣"})
                    Check(PrintRenderer.Text(value,90,12).Width<=90.5,id+" mixed/long text wraps");
                var window=new PrintPreviewWindow(doc,"اختبار المعاينة — "+id);window.Show();
                app.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var viewer=(DocumentViewer)window.FindName("Viewer");
                viewer.Zoom=40;viewer.Zoom=300;viewer.FitToWidth();viewer.NextPage();viewer.PreviousPage();
                Check(paginator.PageSize==original && paginator.PageCount==count,id+" zoom/navigation never repaginates");
                Check(Math.Abs(((Slider)window.FindName("ZoomSlider")).Value-viewer.Zoom)<0.01,id+" zoom indicator stays synchronized");window.Close();
                var pdf=Path.Combine(folder,id+".pdf");PrintRenderer.ExportPdf(doc,pdf,model.DocTitle);
                using(var read=PdfReader.Open(pdf,PdfDocumentOpenMode.Import))
                {
                    Check(read.PageCount==count,id+" PDF page count equals preview");
                    Check(Math.Abs(read.Pages[0].Width.Point-original.Width*72/96)<0.1,id+" PDF paper width");
                    Check(Math.Abs(read.Pages[0].Height.Point-original.Height*72/96)<0.1,id+" PDF paper height");
                }
                foreach(int p in new[]{0,count-1}.Distinct())
                {
                    var bitmap=PrintRenderer.RenderPage(paginator,p,144);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream=File.Create(Path.Combine(folder,$"{id}-page-{p+1}.png"));encoder.Save(stream);
                    Check(stream.Length>1000,id+" rendered image "+p);
                }
                results.Add(new {id,pages=count,width=original.Width,height=original.Height});
            }
            File.WriteAllText(Path.Combine(folder,"results.json"),JsonSerializer.Serialize(new{passed=true,checks=_checks,results},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS: {_checks} WPF checks. No operational database or printer jobs used. Visually inspect the PDFs and PNGs for Arabic and row boundaries.");return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(folder,"failure.txt"),ex.ToString());return 1; }
        finally {app.Shutdown();}
    }
}
