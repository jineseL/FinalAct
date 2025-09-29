using UnityEngine;

public interface IDamageable
{
    /// True if the object can still take meaningful damage.
    bool IsAlive { get; }

    /// Server-side damage entry. Implementations should no-op on clients.
    void TakeDamage(float amount);
}
