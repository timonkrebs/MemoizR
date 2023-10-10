# MemoizR:AsyncLock

AsyncAsymmetricLock should only be used in accordance with structured sequential concurrency.
No Task should be stored in a variable to ensure structured sequential concurrency.

Otherwise it there will be undefined behaviour.