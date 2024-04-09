namespace MemoizR;

public interface IStateGetR<T>
{
    public Task<T> Get();    
}