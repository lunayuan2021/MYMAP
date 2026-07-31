using UnityEngine;

public class WaterFlowPulse : MonoBehaviour
{
    public float speed=.7f;
    public float amplitude=.035f;
    Vector3 startScale;
    void Awake() => startScale=transform.localScale;
    void Update()
    {
        float pulse=1f+Mathf.Sin(Time.time*speed)*amplitude;
        transform.localScale=new Vector3(startScale.x,pulse*startScale.y,startScale.z);
    }
}