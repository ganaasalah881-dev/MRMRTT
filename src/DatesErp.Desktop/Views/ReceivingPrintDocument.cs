using System.Windows.Documents;
using DatesErp.Desktop.Printing;
namespace DatesErp.Desktop.Views;
public static class ReceivingPrintDocument
{
    public static FixedDocument Build(ReceivingPrintModel model) => PhasePrint.Build(ReceivingPrintDesign.Create(model));
}
