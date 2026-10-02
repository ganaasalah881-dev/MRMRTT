using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>Preview immutable physical pages; all output routes use this exact snapshot.</summary>
public partial class PrintPreviewWindow : Window
{
    private readonly FixedDocument _doc;
    public PrintPreviewWindow(FixedDocument doc,string title)
    {
        ArgumentNullException.ThrowIfNull(doc);
        InitializeComponent();_doc=doc;PreviewTitle.Text=title;Title="معاينة قبل الطباعة — "+title;
        Viewer.Document=doc;PageCountText.Text=doc.Pages.Count.ToString();
        var zoomProperty=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DocumentViewer.ZoomProperty,typeof(DocumentViewer));
        EventHandler syncZoom=(_,_)=>
        {
            double value=Math.Clamp(Viewer.Zoom,40,300);
            if(Math.Abs(ZoomSlider.Value-value)>0.001)ZoomSlider.Value=value;
            if(Math.Abs(Viewer.Zoom-value)>0.001)Viewer.Zoom=value;
            ZoomLabel.Text=$"{value:0}%";
        };
        zoomProperty.AddValueChanged(Viewer,syncZoom);
        Closed+=(_,_)=>zoomProperty.RemoveValueChanged(Viewer,syncZoom);
        Loaded+=(_,_)=>Fit_Click(this,new RoutedEventArgs());
    }
    private void ViewerPrint_Executed(object sender,ExecutedRoutedEventArgs e) { e.Handled=true;Print_Click(sender,e); }
    private void Print_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            bool landscape=_doc.DocumentPaginator.PageSize.Width>_doc.DocumentPaginator.PageSize.Height;
            var dlg=new PrintDialog { UserPageRangeEnabled=true,MinPage=1,MaxPage=(uint)_doc.Pages.Count,
                PrintTicket=new PrintTicket { PageMediaSize=new PageMediaSize(PageMediaSizeName.ISOA4),PageOrientation=landscape?PageOrientation.Landscape:PageOrientation.Portrait } };
            if(dlg.ShowDialog()!=true)return;
            // Respect user-selected paper/orientation AFTER the dialog; do not silently overwrite it.
            var caps=dlg.PrintQueue.GetPrintCapabilities(dlg.PrintTicket);
            var area=caps.PageImageableArea;
            double x=area?.OriginWidth??0,y=area?.OriginHeight??0;
            double width=area?.ExtentWidth??dlg.PrintableAreaWidth,height=area?.ExtentHeight??dlg.PrintableAreaHeight;
            var source=_doc.DocumentPaginator;
            var fit=PrintLayout.Fit(source.PageSize.Width,source.PageSize.Height,x,y,width,height);
            int first=dlg.PageRangeSelection==PageRangeSelection.UserPages?Math.Clamp(dlg.PageRange.PageFrom,1,_doc.Pages.Count):1;
            int last=dlg.PageRangeSelection==PageRangeSelection.UserPages?Math.Clamp(dlg.PageRange.PageTo,first,_doc.Pages.Count):_doc.Pages.Count;
            var size=new Size(Math.Max(x+width,caps.OrientedPageMediaWidth??0),Math.Max(y+height,caps.OrientedPageMediaHeight??0));
            dlg.PrintDocument(new PrinterPaginator(source,fit,size,first-1,last-first+1),PreviewTitle.Text);
        }
        catch(Exception ex) { MessageBox.Show(this,"تعذرت الطباعة؛ لم تتغير المعاينة.\n"+ex.Message,"الطباعة",MessageBoxButton.OK,MessageBoxImage.Warning); }
    }
    private sealed class PrinterPaginator(DocumentPaginator source,PrintPlacement fit,Size size,int first,int count):DocumentPaginator
    {
        public override bool IsPageCountValid=>true;
        public override int PageCount=>count;
        public override Size PageSize { get=>size;set { } }
        public override IDocumentPaginatorSource Source=>source.Source;
        public override DocumentPage GetPage(int number)
        {
            if(number<0 || number>=count)return DocumentPage.Missing;
            var page=source.GetPage(number+first);var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White,null,new Rect(size));
                dc.DrawRectangle(new VisualBrush(page.Visual) {Stretch=Stretch.Fill},null,
                    new Rect(fit.X,fit.Y,page.Size.Width*fit.Scale,page.Size.Height*fit.Scale));
            }
            return new DocumentPage(visual,size,new Rect(size),new Rect(size));
        }
    }
    private void Pdf_Click(object sender,RoutedEventArgs e)
    {
        var dlg=new Microsoft.Win32.SaveFileDialog {Filter="ملف PDF|*.pdf",FileName=MakeSafe(PreviewTitle.Text)+".pdf"};
        if(dlg.ShowDialog()!=true)return;
        try { Mouse.OverrideCursor=Cursors.Wait;PrintRenderer.ExportPdf(_doc,dlg.FileName,PreviewTitle.Text);AppContainer.Get<DialogService>().Toast("تم حفظ النسخة المطابقة للمعاينة:\n"+dlg.FileName); }
        catch(Exception ex) { MessageBox.Show(this,"تعذر حفظ PDF:\n"+ex.Message,"PDF",MessageBoxButton.OK,MessageBoxImage.Warning); }
        finally { Mouse.OverrideCursor=null; }
    }
    private void ApplyZoom(double value) { if(Viewer==null)return;Viewer.Zoom=value;if(ZoomLabel!=null)ZoomLabel.Text=$"{value:0}%"; }
    private void Zoom_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)=>ApplyZoom(e.NewValue);
    private void ZoomIn_Click(object sender,RoutedEventArgs e)=>ZoomSlider.Value=Math.Min(300,ZoomSlider.Value+20);
    private void ZoomOut_Click(object sender,RoutedEventArgs e)=>ZoomSlider.Value=Math.Max(40,ZoomSlider.Value-20);
    private void Reset_Click(object sender,RoutedEventArgs e)=>ZoomSlider.Value=100;
    private void Fit_Click(object sender,RoutedEventArgs e) { Viewer.FitToWidth();ZoomSlider.Value=Math.Clamp(Viewer.Zoom,40,300); }
    private void Previous_Click(object sender,RoutedEventArgs e)=>Viewer.PreviousPage();
    private void Next_Click(object sender,RoutedEventArgs e)=>Viewer.NextPage();
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private static string MakeSafe(string s)
    { var safe=string.Concat((s??"").Where(c=>!Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');return string.IsNullOrWhiteSpace(safe)?"document":safe; }
}
