sampler2D Image : register(s0);

float4 main(float2 uv : TEXCOORD0) : COLOR0 {
    const float2 size = float2(328, 42);
    const float radius = 11;
    float2 p = (uv - .5) * size;
    float2 q = abs(p) - (.5 * size - radius);
    float edge = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
    float rim = saturate(-edge / 7);
    float2 normal = normalize(p + .001);
    float2 ray = clamp(uv - normal * (1 - rim) * (1 - rim) * float2(.016,.13), .01, .99);
    float2 dx = float2(.009,0), dy = float2(0,.07);
    float3 video = .36 * tex2D(Image,ray).rgb +
        .16 * (tex2D(Image,saturate(ray+dx)).rgb + tex2D(Image,saturate(ray-dx)).rgb +
               tex2D(Image,saturate(ray+dy)).rgb + tex2D(Image,saturate(ray-dy)).rgb);
    float highlight = (1 - smoothstep(-1.4, -.15, -abs(edge))) * (.09 + .12 * (1 - uv.y));
    float3 tint = .06 + video * .30 + highlight;
    float alpha = saturate(.4 - edge);
    return float4(tint * alpha, alpha);
}