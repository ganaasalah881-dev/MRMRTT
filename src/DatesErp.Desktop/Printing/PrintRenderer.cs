using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DatesErp.Desktop.Printing;

/// <summary>One frozen page tree for preview, printer and PDF. Arabic shaping is performed by WPF, not PDFsharp.</summary>
public static class PrintRenderer
{
    private static readonly Brush Ink = Brush("#1E293B"), Navy = Brush("#14532D"), GreenDark = Brush("#0B3D1F"), Gold = Brush("#FCD34D"), Muted = Brush("#64748B");
    private static readonly LinearGradientBrush HeaderGrad;
    static PrintRenderer()
    {
        HeaderGrad=new LinearGradientBrush(Color.FromRgb(0x14,0x53,0x2D),Color.FromRgb(0x0B,0x3D,0x1F),new Point(0,0),new Point(1,1));
        HeaderGrad.Freeze();
    }
    private static Brush Brush(string hex) { var b=(SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
    private static Brush Brush(Color c,double alpha=1){var b=new SolidColorBrush(c);b.Opacity=alpha;b.Freeze();return b;}
    private static Color StatusColor(string s)=> s==null?Color.FromRgb(0x15,0x80,0x3D)
        :s.Contains("مسودة")?Color.FromRgb(0x92,0x40,0x0E):s.Contains("ملغى")?Color.FromRgb(0xB9,0x1C,0x1C):s.Contains("مقفل")?Color.FromRgb(0x47,0x55,0x69):Color.FromRgb(0x15,0x80,0x3D);
    private static Color StatusEdgeColor(string s)=> s==null?Color.FromRgb(0x86,0xEF,0xAC)
        :s.Contains("مسودة")?Color.FromRgb(0xFC,0xD3,0x4D):s.Contains("ملغى")?Color.FromRgb(0xFC,0xA5,0xA5):s.Contains("مقفل")?Color.FromRgb(0xCB,0xD5,0xE1):Color.FromRgb(0x86,0xEF,0xAC);
    private static Color StatusBgColor(string s)=> s==null?Color.FromRgb(0xDC,0xFC,0xE7)
        :s.Contains("مسودة")?Color.FromRgb(0xFE,0xF3,0xC7):s.Contains("ملغى")?Color.FromRgb(0xFE,0xE2,0xE2):s.Contains("مقفل")?Color.FromRgb(0xE2,0xE8,0xF0):Color.FromRgb(0xDC,0xFC,0xE7);
    private static Brush StatusBrush(string s)=>Brush(StatusColor(s));
    private static Brush StatusTint(string s)=>Brush(StatusBgColor(s));
    private static Brush StatusEdge(string s)=>Brush(StatusEdgeColor(s));
    private static bool IsStatusText(string s)=> s!=null && s.Length<=14
        && (s.Contains("مسودة")||s.Contains("ملغى")||s.Contains("مقفل")||s.Contains("معتمد")||s.Contains("تم الصرف")||s.Contains("محتسب"));
    public static FormattedText Text(string value, double width, double size, bool bold=false, Brush foreground=null, TextAlignment align=TextAlignment.Right)
    {
        var ft=new FormattedText(value??"",CultureInfo.GetCultureInfo("ar-YE"),FlowDirection.RightToLeft,
            new Typeface(new FontFamily("Segoe UI, Tahoma"),FontStyles.Normal,bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal),size,foreground??Ink,1);
        ft.MaxTextWidth=Math.Max(1,width); ft.LineHeight=Math.Max(PrintLayout.LineHeight,Math.Ceiling(size*1.35));
        ft.TextAlignment=align; ft.Trimming=TextTrimming.None;
        return ft;
    }
    public static FixedDocument Build(PrintSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        double w=spec.Landscape?PrintLayout.A4Height:PrintLayout.A4Width;
        double h=spec.Landscape?PrintLayout.A4Width:PrintLayout.A4Height;
        double contentW=w-PrintLayout.Margin*2;
        // §43: ترويسة ثلاثية المناطق على غرار القوالب المرجعية المعتمدة:
        // يمين: هوية المنشأة — وسط: الشعار + عنوان المستند المؤطّر + شارة الحالة — يسار: بيانات الطباعة.
        var now=DateTime.Now;
        double colW=contentW/3.0, centerColX=PrintLayout.Margin+colW;
        var company=Text(spec.Company,colW-8,15,true,Navy);
        var coSub=Text("نظام التصنيع — إدارة العمليات المتكاملة",colW-8,10,false,Muted);
        var en1=Text("MfgSystem — Manufacturing Operations",colW-8,10,false,Muted,TextAlignment.Left);
        var en2=Text($"Date: {now:dd/MM/yyyy}  •  Time: {now:HH:mm}",colW-8,10,false,Muted,TextAlignment.Left);
        bool hasLogo=spec.Logo is {Length:>0};
        double logoH=hasLogo?52:0;
        var title=Text(spec.Title,colW-16,17,true,Navy,TextAlignment.Center);
        double badgeW=title.Width+36, badgeH=title.Height+12;
        var subInfo=Text($"رقم السند: {spec.Number}   •   نسخة: {spec.CapturedAt:dd/MM/yyyy HH:mm}",colW-8,10.5,true,Muted,TextAlignment.Center);
        var status=Text(spec.Status??"",220,10.5,true,StatusBrush(spec.Status));
        bool hasStatus=!string.IsNullOrEmpty(spec.Status);
        double pillW=hasStatus?status.Width+16:0, pillH=status.Height+6;
        double badgeY=PrintLayout.Margin+logoH+(hasLogo?6:0);
        double subY=badgeY+badgeH+8;
        double totalW=subInfo.Width+(hasStatus?8+pillW:0), startX=centerColX+(colW-totalW)/2;
        double pillX=startX+subInfo.Width+8;
        double headerBottom=subY+Math.Max(subInfo.Height,pillH)+14;
        var pages=PrintLayout.Paginate(spec,headerBottom,(s,width,size,bold)=>
            Math.Max(1,(int)Math.Ceiling(Text(s,width,size,bold).Height/PrintLayout.LineHeight)));
        var doc=new FixedDocument(); doc.DocumentPaginator.PageSize=new Size(w,h);
        BitmapImage logo=null;
        if(spec.Logo is {Length:>0})
        {
            try { using var stream=new MemoryStream(spec.Logo); logo=new BitmapImage(); logo.BeginInit();logo.CacheOption=BitmapCacheOption.OnLoad;logo.StreamSource=stream;logo.EndInit();logo.Freeze(); }
            catch { logo=null; } // Company name remains visible if its optional logo is corrupt.
        }
        // §45 — QR للمستند: رقم السند + لحظة الإصدار، يُمسح بالهاتف للتحقق السريع من النسخة الورقية.
        // يُبنى مرة واحدة لكل مستند، وأي فشل في توليده لا يحجب الطباعة (تحسين لا وظيفة حرجة).
        BitmapSource qr=null;
        try
        {
            using var qgen=new QRCoder.QRCodeGenerator();
            using var qdata=qgen.CreateQrCode($"{spec.Number}|{spec.CapturedAt:yyyyMMddHHmm}",QRCoder.QRCodeGenerator.ECCLevel.M);
            var qpng=new QRCoder.PngByteQRCode(qdata).GetGraphic(4);
            using var qms=new MemoryStream(qpng);
            var qi=new BitmapImage(); qi.BeginInit(); qi.CacheOption=BitmapCacheOption.OnLoad; qi.StreamSource=qms; qi.EndInit(); qi.Freeze(); qr=qi;
        }
        catch { qr=null; }
        for(int pi=0;pi<pages.Count;pi++)
        {
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White,null,new Rect(0,0,w,h));
                // §43: إطار خارجي مدوّر بلون الهوية — يحيط النموذج كالفورمات الرسمية
                dc.DrawRoundedRectangle(null,new Pen(Navy,1.4),new Rect(8,8,w-16,h-16),10,10);
                // المنطقة اليمنى: هوية المنشأة
                double rightX=w-PrintLayout.Margin-colW+8;
                dc.DrawText(company,new Point(rightX,PrintLayout.Margin));
                dc.DrawText(coSub,new Point(rightX,PrintLayout.Margin+company.Height+2));
                // المنطقة اليسرى: بيانات الطباعة (لاتيني)
                dc.DrawText(en1,new Point(PrintLayout.Margin,PrintLayout.Margin));
                dc.DrawText(en2,new Point(PrintLayout.Margin,PrintLayout.Margin+en1.Height+2));
                // المنطقة الوسطى: الشعار ثم عنوان المستند داخل شارة مؤطّرة ثم رقم السند وشارة الحالة
                if(logo!=null)
                {
                    double scale=Math.Min(140/logo.Width,52/logo.Height);
                    dc.DrawImage(logo,new Rect(centerColX+(colW-logo.Width*scale)/2,PrintLayout.Margin,logo.Width*scale,logo.Height*scale));
                }
                double badgeX=centerColX+(colW-badgeW)/2;
                dc.DrawRoundedRectangle(Brush("#F0FDF4"),new Pen(Navy,2),new Rect(badgeX,badgeY,badgeW,badgeH),8,8);
                dc.DrawText(title,new Point(badgeX+18,badgeY+6));
                dc.DrawText(subInfo,new Point(startX,subY));
                if(hasStatus)
                {
                    dc.DrawRoundedRectangle(StatusTint(spec.Status),new Pen(StatusEdge(spec.Status),1),
                        new Rect(pillX,subY-2,pillW,pillH),6,6);
                    dc.DrawText(status,new Point(pillX+8,subY+1));
                }
                // فاصل الترويسة: خط أخضر ثقيل واحد كهوية القوالب المرجعية
                dc.DrawLine(new Pen(Navy,2.5),new Point(PrintLayout.Margin,headerBottom-8),new Point(w-PrintLayout.Margin,headerBottom-8));
                foreach(var band in pages[pi].Slices)
                {
                    bool bold=band.Style is "header" or "title" or "total" or "signature";
                    Brush bg=band.Style switch {"header"=>HeaderGrad,"title"=>Brush("#F0FDF4"),"total"=>Brush("#FEF3C7"),"signature"=>Brushes.White,"meta"=>Brush("#F8FAFC"),"kpi"=>Brushes.White,_=>band.RowIndex%2==0?Brushes.White:Brush("#F8FAFC")};
                    if(band.Style=="total")
                        dc.DrawLine(new Pen(Gold,1.2),new Point(PrintLayout.Margin,band.Y),new Point(w-PrintLayout.Margin,band.Y));
                    if(band.Style=="signature")
                        dc.DrawLine(new Pen(Brush("#64748B"),1.2){DashStyle=DashStyles.Dash},new Point(PrintLayout.Margin,band.Y),new Point(w-PrintLayout.Margin,band.Y));
                    // خطوط مصمتة كافية للمسح بالأبيض والأسود بدل #CBD5E1 الباهت على الأبيض.
                    if(band.Style=="meta")
                        dc.DrawRoundedRectangle(Brush("#F8FAFC"),new Pen(Brush("#64748B"),1.1),
                            new Rect(PrintLayout.Margin,band.Y,contentW,band.Height),8,8);
                    double x=w-PrintLayout.Margin;
                    for(int c=0;c<band.Cells.Length;c++)
                    {
                        x-=band.Widths[c]; var cell=new Rect(x,band.Y,band.Widths[c],band.Height);
                        if(band.Style is not ("meta" or "kpi"))
                            dc.DrawRectangle(bg,new Pen(Brush("#64748B"),0.9),cell); // حد واضح لكل صف وعمود، حتى في PDF الرمادي.
                        if(band.Style=="kpi")
                        {
                            // §43 كل زوج (بيان،قيمة) صندوق بحد علوي ملوّن كـ summary-boxes المرجعية
                            if(c%2==1)
                            {
                                double boxW=band.Widths[c]+band.Widths[c-1];
                                int k=(c-1)/2;
                                Brush top=k switch {0=>Navy,1=>Brush("#16A34A"),2=>Brush("#D97706"),_=>Brush("#1E40AF")};
                                dc.DrawRoundedRectangle(Brush("#F8FAFC"),new Pen(Brush("#64748B"),1.1),new Rect(x,band.Y+1,boxW,band.Height-2),6,6);
                                dc.DrawRectangle(top,null,new Rect(x+3,band.Y+2,boxW-6,3));
                                var lb=Text(band.Cells[c-1],boxW-12,10.5,false,Muted,TextAlignment.Center);
                                var vl=Text(band.Cells[c],boxW-12,14,true,top,TextAlignment.Center);
                                dc.DrawText(lb,new Point(x+6,band.Y+8));
                                dc.DrawText(vl,new Point(x+6,band.Y+8+lb.Height+2));
                            }
                            continue;
                        }
                        if(band.Style=="signature")
                        {
                            // §43 الدور بأخضر ثقيل ثم سطور الفراغ بخط أهدأ — كـ sign-box المرجعية
                            var lines=(band.Cells[c]??"").Split('\n');
                            for(int li=0;li<band.LineCount;li++)
                            {
                                int idx=band.FirstLine+li; if(idx>=lines.Length) break;
                                dc.DrawText(Text(lines[idx],band.Widths[c]-2*PrintLayout.Padding,idx==0?11:10,idx==0,idx==0?Navy:Muted),
                                    new Point(x+PrintLayout.Padding,band.Y+PrintLayout.Padding+li*PrintLayout.LineHeight));
                            }
                            continue;
                        }
                        // Clip each line fragment only; all other lines are rendered on following pages.
                        dc.PushClip(new RectangleGeometry(new Rect(x+PrintLayout.Padding,band.Y+PrintLayout.Padding,band.Widths[c]-2*PrintLayout.Padding,band.LineCount*PrintLayout.LineHeight)));
                        bool isMeta=band.Style=="meta";
                        Brush fg=band.Style=="header"?Brushes.White:band.Style=="title"?Navy:band.Style=="total"?Brush("#92400E"):isMeta&&c%2==0?Muted:Ink;
                        var text=Text(band.Cells[c],band.Widths[c]-2*PrintLayout.Padding,isMeta?(c%2==0?10.5:11):band.FontSize,isMeta?c%2==1:bold,fg);
                        // §43 شارة حالة داخل الخلية: حبة ملوّنة موسطة كـ badge المرجعية
                        if(band.Style is null or "" && text.Height<=PrintLayout.LineHeight && IsStatusText(band.Cells[c]))
                        {
                            var bt=Text(band.Cells[c],band.Widths[c]-2*PrintLayout.Padding,10,true,StatusBrush(band.Cells[c]));
                            double pw=bt.Width+12, ph=bt.Height+4;
                            if(pw<=band.Widths[c]-4)
                            {
                                double px=x+(band.Widths[c]-pw)/2, py=band.Y+(band.Height-ph)/2;
                                dc.DrawRoundedRectangle(StatusTint(band.Cells[c]),new Pen(StatusEdge(band.Cells[c]),1),new Rect(px,py,pw,ph),4,4);
                                dc.DrawText(bt,new Point(px+6,py+2));
                                dc.Pop();
                                continue;
                            }
                        }
                        // Repeat a short first-cell identifier on continuation pages for traceability.
                        double offset=band.FirstLine*PrintLayout.LineHeight;
                        if(c==0 && band.Cells.Length>1 && text.Height<=PrintLayout.LineHeight) offset=0;
                        dc.DrawText(text,new Point(x+PrintLayout.Padding,band.Y+PrintLayout.Padding-offset));
                        dc.Pop();
                    }
                }
                dc.DrawLine(new Pen(Brush("#64748B"),1),new Point(PrintLayout.Margin,h-40),new Point(w-PrintLayout.Margin,h-40));
                dc.DrawText(Text($"{spec.Company} — نظام التصنيع المتكامل v1.50.74",contentW-52,9.5,false,Muted),new Point(PrintLayout.Margin,h-30));
                dc.DrawText(Text($"المستخدم: {Environment.UserName}  •  تاريخ الطباعة: {now:dd/MM/yyyy HH:mm}  •  صفحة {pi+1} من {pages.Count}",contentW-52,9.5,false,Muted,TextAlignment.Left),new Point(PrintLayout.Margin,h-30));
                if(qr!=null) dc.DrawImage(qr,new Rect(w-PrintLayout.Margin-40,h-40,40,40)); // §45
            }
            var page=new FixedPage { Width=w,Height=h,Background=Brushes.White,FlowDirection=FlowDirection.LeftToRight };
            page.Children.Add(new DrawingPage(visual,w,h));
            page.Measure(new Size(w,h));page.Arrange(new Rect(0,0,w,h));page.UpdateLayout();
            var content=new PageContent();((IAddChild)content).AddChild(page);doc.Pages.Add(content);
        }
        return doc;
    }
    private sealed class DrawingPage : FrameworkElement
    {
        private readonly DrawingVisual _visual;
        public DrawingPage(DrawingVisual visual,double width,double height) { _visual=visual;Width=width;Height=height;AddVisualChild(visual); }
        protected override int VisualChildrenCount=>1;
        protected override Visual GetVisualChild(int index)=>index==0?_visual:throw new ArgumentOutOfRangeException(nameof(index));
    }
    public static BitmapSource RenderPage(DocumentPaginator paginator,int index,double dpi=300)
    {
        var page=paginator.GetPage(index);
        int width=(int)Math.Ceiling(page.Size.Width*dpi/96),height=(int)Math.Ceiling(page.Size.Height*dpi/96);
        var bitmap=new RenderTargetBitmap(width,height,dpi,dpi,PixelFormats.Pbgra32);
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(page.Size));
            dc.DrawRectangle(new VisualBrush(page.Visual) {Stretch=Stretch.Fill},null,new Rect(page.Size));
        }
        bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    /// <summary>Faithful 300 DPI image PDF (not searchable text). Atomic replacement avoids partial output.</summary>
    public static void ExportPdf(FixedDocument document,string path,string title)
    {
        var paginator=document.DocumentPaginator; paginator.ComputePageCount();
        if(paginator.PageCount<1) throw new InvalidOperationException("لا صفحات للتصدير.");
        var full=Path.GetFullPath(path); var temp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using var pdf=new PdfDocument();pdf.Info.Title=title;pdf.Info.Subject="نسخة مطابقة للمعاينة — صور 300 DPI";
            for(int i=0;i<paginator.PageCount;i++)
            {
                var image=RenderPage(paginator,i);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using var buffer=new MemoryStream();encoder.Save(buffer);buffer.Position=0;
                var page=pdf.AddPage();page.Width=PdfSharp.Drawing.XUnit.FromPoint(paginator.PageSize.Width*72/96);page.Height=PdfSharp.Drawing.XUnit.FromPoint(paginator.PageSize.Height*72/96);
                using var ximage=XImage.FromStream(buffer);using var gfx=XGraphics.FromPdfPage(page);
                gfx.DrawImage(ximage,0,0,page.Width.Point,page.Height.Point);
            }
            pdf.Save(temp);File.Move(temp,full,true);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
