using UnityEngine;

public class Weapon1script : Weapons
{
    // double barrel weapon
    

    [Header("Blast Settings")]
    public Transform firePoint;        // where the blast originates
    public float blastRadius = 5f;     // how far the blast reaches
    public float blastAngle = 90f;     // half-angle of the semicircle (90 = 180° cone)
    public float playerKnockbackForce = 20f;
    public float enemyDamage = 25f;
    public float selfRecoilForce = 15f;

    [Header("Fps View")]
    //fps view will only be for looks
    public Transform FpsShootPoint1;
    public Transform FpsShootPoint2;
    public GameObject ShootVFX1;

    public override void Fire()
    {
        if (isAttacking || isReloading) return;

        isAttacking = true;

        FpsFire();

    }
    public void WorldFire()
    {
        // === 1. Detect targets in radius ===
        Collider[] hits = Physics.OverlapSphere(firePoint.position, blastRadius);

        foreach (var hit in hits)
        {
            Vector3 dirToTarget = (hit.transform.position - firePoint.position).normalized;
            float angle = Vector3.Angle(firePoint.forward, dirToTarget);

            // Only process if inside the blast cone
            if (angle <= blastAngle)
            {
                // === 2. Handle players ===
                if (hit.CompareTag("Player") && hit.gameObject != owner.gameObject)
                {
                    CharacterController cc = hit.GetComponent<CharacterController>();
                    if (cc != null)
                    {
                        // Apply knockback (direction from firePoint)
                        Vector3 knockbackDir = dirToTarget;
                        KnockbackTarget(cc, knockbackDir * playerKnockbackForce);
                    }
                }

                // === 3. Handle enemies ===
                /*if (hit.CompareTag("Enemy"))
                {
                    Enemy enemy = hit.GetComponent<Enemy>();
                    if (enemy != null)
                    {
                        enemy.TakeDamage(enemyDamage);
                    }
                }*/
            }
        }

        // === 4. Apply recoil to self ===
        CharacterController myCC = owner.GetComponent<CharacterController>();
        if (myCC != null)
        {
            Vector3 recoilDir = -firePoint.forward; // backwards
            KnockbackTarget(myCC, recoilDir * selfRecoilForce);
        }

        // TODO: play VFX / SFX here

        // Reset attack flag after attackSpeed
        //Invoke(nameof(ResetAttack), attackSpeed);
    }
    public void FpsFire()
    {
        fpsAnimator.Play("FpsDoubleBarrelFiring"); //play fps animation
        if (currentAmmoCount % 2 ==0)
        {
            Instantiate(ShootVFX1, FpsShootPoint1.position,FpsShootPoint1.rotation);
        }
        else Instantiate(ShootVFX1, FpsShootPoint2.position, FpsShootPoint2.rotation);
        //todo sfx
        //
    }

    private void KnockbackTarget(CharacterController cc, Vector3 force)
    {
        // CharacterController doesn’t support AddForce,
        // so we simulate knockback by setting velocity.
        PlayerMotor motor = cc.GetComponent<PlayerMotor>();
        if (motor != null)
        {
            motor.ApplyExternalForce(force);
        }
    }

    private void ResetAttack()
    {
        isAttacking = false;
    }
}
