namespace MemoizR;

internal interface IMemoHandlR
{
    IMemoizR[] Observers { get; set; }
    IMemoHandlR[] Sources { get; set; }
}
