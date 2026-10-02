using DatesErp.Desktop.Views;

namespace DatesErp.Desktop.Printing;

public static class PrintSchema
{
    public static PrintSpec FromPhase(PhaseDocModel m)
    {
        if (m.Columns.Length == 0 && m.Rows.Count > 0 || m.SecondColumns.Length == 0 && m.SecondRows.Count > 0)
            throw new ArgumentException("بنود طباعة بلا عناوين أعمدة؛ لا يمكن إسقاطها من المستند.");
        // §v1.50.25: التصميم المعتمد — كل النماذج الرسمية A4 عمودي؛ أُلغي الانقلاب الأفقي التلقائي للجداول العريضة.
        var spec=new PrintSpec {Company=m.CompanyNameAr??"",Logo=m.LogoBytes?.ToArray(),Title=m.DocTitle,Number=m.DocNo,
            Status=m.StatusAr,Landscape=m.Landscape,CapturedAt=m.CapturedAt};
        var fields=m.Info.ToList();
        if(!string.IsNullOrWhiteSpace(m.CompanyAddress))fields.Add(("عنوان المنشأة",m.CompanyAddress));
        if(!string.IsNullOrWhiteSpace(m.CompanyPhone))fields.Add(("هاتف المنشأة",m.CompanyPhone));
        void Pairs(string title,List<(string Label,string Value)> values,string style)
        {
            if(values.Count==0)return;
            var s=new PrintSection {Title=title,Style=style,Weights=new[]{1d,2d,1d,2d}};
            for(int i=0;i<values.Count;i+=2)
                s.Rows.Add(new[]{values[i].Label,values[i].Value,i+1<values.Count?values[i+1].Label:"",i+1<values.Count?values[i+1].Value:""});
            spec.Sections.Add(s);
        }
        Pairs("بيانات المستند",fields,"meta");
        if(m.Columns.Length>0)spec.Sections.Add(new PrintSection {Title=m.MainTitle,Columns=m.Columns.ToArray(),Weights=m.ColumnWeights?.ToArray(),Rows=m.Rows.Select(r=>r.Select(PrintLayout.Format).ToArray()).ToList()});
        if(m.SecondColumns.Length>0)spec.Sections.Add(new PrintSection {Title=m.SecondTitle,Columns=m.SecondColumns.ToArray(),Rows=m.SecondRows.Select(r=>r.Select(PrintLayout.Format).ToArray()).ToList()});
        foreach(var s in m.ExtraSections)spec.Sections.Add(new PrintSection {Title=s.Title,Columns=s.Columns.ToArray(),Rows=s.Rows.Select(r=>r.ToArray()).ToList(),Weights=s.Weights?.ToArray(),Style=s.Style});
        // Explicit totals only: never sum carton prices, rates, percentages, identifiers or unlike units.
        Pairs("الإجماليات",m.Totals,"total");
        if(!string.IsNullOrWhiteSpace(m.Notes))spec.Sections.Add(new PrintSection {Title="ملاحظات المستند",Rows=new(){new[]{m.Notes}}});
        if(m.Signatures.Count>0)spec.Sections.Add(new PrintSection {Title="التوقيعات والاستلام",Style="signature",Rows=new(){m.Signatures.Select(s=>$"{s}\nالاسم: ........................\nالتوقيع: .....................\nالتاريخ: ......................").ToArray()}});
        if(!string.IsNullOrWhiteSpace(m.FooterNote))spec.Sections.Add(new PrintSection {Title="بيان النسخة",Rows=new(){new[]{m.FooterNote}}});
        return spec;
    }
}
