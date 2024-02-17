namespace MemoizR;

internal interface IMemoHandlR
{
    WeakReference<IMemoizR>[] Observers { get; set; }
    IMemoHandlR[] Sources { get; set; }
}
