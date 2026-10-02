using System.Windows.Documents;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

public static class PhasePrint
{
    public static FixedDocument Build(PhaseDocModel m)
    {
        if(m.CompanyNameAr==null)
        {
            m.CompanyNameAr=CompanyIdentity.NameAr;m.CompanyAddress=CompanyIdentity.Address;
            m.CompanyPhone=CompanyIdentity.Phone;m.LogoBytes=CompanyIdentity.LogoBytes;
        }
        return PrintRenderer.Build(PrintSchema.FromPhase(m));
    }
    public static void ExportPdf(PhaseDocModel m,string path)
    {
        // Build includes m.MainTitle, m.SecondTitle, m.SecondColumns and m.SecondRows via the shared schema.
        // No separate PDF layout, no omitted notes and no 46-character truncation.
        PrintRenderer.ExportPdf(Build(m),path,$"{m.DocTitle} {m.DocNo}");
    }
}
