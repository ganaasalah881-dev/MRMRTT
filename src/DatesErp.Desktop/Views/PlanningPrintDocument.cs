using DatesErp.Desktop.Printing;
using System.Windows.Documents;
namespace DatesErp.Desktop.Views;
public static class PlanningPrintDocument
{
    public static FixedDocument Build(PlanningPrintModel model) => PhasePrint.Build(PlanningPrintDesign.Create(model));
}
