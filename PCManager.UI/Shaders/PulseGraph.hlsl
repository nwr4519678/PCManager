cbuffer UniformBlock : register(b0)
{
    float Time;
    float ResourceUsage;
};

struct VS_INPUT
{
    float3 Pos : POSITION;
    float2 TexCoord : TEXCOORD;
};

struct PS_INPUT
{
    float4 Pos : SV_POSITION;
    float2 TexCoord : TEXCOORD;
};

PS_INPUT VSMain(VS_INPUT input)
{
    PS_INPUT output;
    // Simple pulse deformation based on ResourceUsage (0-100)
    float pulse = sin(Time * 5.0) * (ResourceUsage / 100.0) * 0.1;
    output.Pos = float4(input.Pos.x, input.Pos.y + pulse, input.Pos.z, 1.0);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PSMain(PS_INPUT input) : SV_Target
{
    // Color code based on usage map: Green -> Red
    float r = clamp(ResourceUsage / 50.0, 0.0, 1.0);
    float g = clamp(2.0 - (ResourceUsage / 50.0), 0.0, 1.0);
    return float4(r, g, 0.2, 1.0);
}
