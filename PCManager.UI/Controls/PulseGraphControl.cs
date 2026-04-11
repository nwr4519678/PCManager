using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace PCManager.UI.Controls;

/// <summary>
/// GPU-accelerated "Matrix Heartbeat" pulse graph using OpenGL/Silk.NET.
/// Renders Tron-style scrolling grid with dual oscillating waveforms (CPU + RAM).
/// </summary>
public class PulseGraphControl : OpenGlControlBase
{
    private GL? _gl;
    private uint _program;
    private uint _vbo;
    private uint _vao;
    private bool _initialized = false;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Smoothed values for Lerp interpolation (prevents jarring jumps)
    private float _smoothCpu = 0f;
    private float _smoothRam = 0f;

    // ─── Styled Properties ───────────────────────────────────────────────────

    public static readonly StyledProperty<double> CpuUsageProperty =
        AvaloniaProperty.Register<PulseGraphControl, double>(nameof(CpuUsage), 0.0);

    public double CpuUsage
    {
        get => GetValue(CpuUsageProperty);
        set => SetValue(CpuUsageProperty, value);
    }

    public static readonly StyledProperty<double> RamUsageProperty =
        AvaloniaProperty.Register<PulseGraphControl, double>(nameof(RamUsage), 0.0);

    public double RamUsage
    {
        get => GetValue(RamUsageProperty);
        set => SetValue(RamUsageProperty, value);
    }

    // ─── GLSL Shaders ─────────────────────────────────────────────────────────

    private const string VertexShaderSource = @"
        #version 330 core
        layout(location = 0) in vec2 a_Pos;
        out vec2 v_TexCoord;
        void main() {
            gl_Position = vec4(a_Pos, 0.0, 1.0);
            v_TexCoord = a_Pos * 0.5 + 0.5;
        }";

    private const string FragmentShaderSource = @"
        #version 330 core
        in vec2 v_TexCoord;
        out vec4 FragColor;

        uniform float u_Time;
        uniform float u_CpuUsage;
        uniform float u_RamUsage;

        // Simple pseudo-random noise
        float hash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        // Soft glow around a line at position lineY
        float glowLine(float y, float lineY, float width) {
            float d = abs(y - lineY);
            return exp(-d * d / (width * width)) * 1.5;
        }

        void main() {
            vec2 uv = v_TexCoord;
            vec3 col = vec3(0.0);

            // ── 1. Dark Tron-Style Scrolling Grid ──
            float scrollY = u_Time * 0.05;
            float gridX = step(0.97, fract(uv.x * 30.0));
            float gridY = step(0.97, fract((uv.y + scrollY) * 15.0));
            float grid = max(gridX, gridY);
            col += grid * vec3(0.0, 0.15, 0.25);

            // Subtle digital noise flicker
            float noise = hash(uv + vec2(u_Time * 0.01));
            col += noise * 0.015 * vec3(0.0, 0.3, 0.5);

            // ── 2. CPU Waveform (Neon Cyan) ──
            float cpuAmp  = 0.08 + u_CpuUsage * 0.18;
            float cpuFreq = 6.0  + u_CpuUsage * 20.0;
            float cpuShift = u_Time * (1.5 + u_CpuUsage * 3.0);

            float cpuWave =
                sin(uv.x * cpuFreq + cpuShift) * cpuAmp +
                sin(uv.x * cpuFreq * 2.1 + cpuShift * 0.7) * (cpuAmp * 0.4) +
                sin(uv.x * cpuFreq * 0.5 + cpuShift * 1.3) * (cpuAmp * 0.25);

            float cpuLine = 0.65 + cpuWave;
            float cpuGlow = glowLine(uv.y, cpuLine, 0.018 + u_CpuUsage * 0.012);
            vec3 cpuColor = vec3(0.0, 0.9, 1.0) * cpuGlow;

            // CPU alert: shift to red if > 80%
            if (u_CpuUsage > 0.8) {
                cpuColor = mix(cpuColor, vec3(1.0, 0.1, 0.0) * cpuGlow, (u_CpuUsage - 0.8) * 5.0);
            }
            col += cpuColor;

            // ── 3. RAM Waveform (Bright Orange) ──
            float ramAmp  = 0.06 + u_RamUsage * 0.15;
            float ramFreq = 8.0  + u_RamUsage * 15.0;
            float ramShift = u_Time * (1.0 + u_RamUsage * 2.5);

            float ramWave =
                sin(uv.x * ramFreq + ramShift) * ramAmp +
                sin(uv.x * ramFreq * 1.7 + ramShift * 0.9) * (ramAmp * 0.35) +
                cos(uv.x * ramFreq * 0.3 + ramShift * 1.1) * (ramAmp * 0.2);

            float ramLine = 0.35 + ramWave;
            float ramGlow = glowLine(uv.y, ramLine, 0.015 + u_RamUsage * 0.010);
            col += vec3(1.0, 0.45, 0.0) * ramGlow;

            // ── 4. Fake Bloom (post-process neighbor sampling) ──
            float blurSpread = 0.008;
            vec2 offsets[4] = vec2[4](
                vec2( blurSpread, 0.0),
                vec2(-blurSpread, 0.0),
                vec2(0.0,  blurSpread),
                vec2(0.0, -blurSpread)
            );

            for (int i = 0; i < 4; i++) {
                vec2 sampleUV = uv + offsets[i];
                float scpu = glowLine(sampleUV.y, 0.65 + sin(sampleUV.x * cpuFreq + cpuShift) * cpuAmp, 0.022);
                float sram = glowLine(sampleUV.y, 0.35 + sin(sampleUV.x * ramFreq + ramShift) * ramAmp, 0.018);
                col += vec3(0.0, 0.9, 1.0) * scpu * 0.08;
                col += vec3(1.0, 0.45, 0.0) * sram * 0.06;
            }

            // ── 5. Scanline vignette ──
            float scanline = 1.0 - 0.04 * sin(uv.y * 800.0);
            col *= scanline;

            float vignette = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 16.0;
            col *= pow(vignette, 0.15);

            FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
        }";

    // ─── OpenGL Lifecycle ─────────────────────────────────────────────────────

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _gl = GL.GetApi(gl.GetProcAddress);
        Console.WriteLine("[PulseGraphControl] OpenGL initialized.");

        // Full-screen quad (TriangleStrip)
        float[] vertices = { -1f, -1f,  1f, -1f, -1f,  1f,  1f,  1f };

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);

        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0); }
        _gl.EnableVertexAttribArray(0);

        _program = CreateShaderProgram(VertexShaderSource, FragmentShaderSource);
        _initialized = _program != 0;

        Console.WriteLine(_initialized
            ? "[PulseGraphControl] Shader program compiled and linked successfully."
            : "[PulseGraphControl] ERROR: Shader program failed to link.");
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_initialized || _gl == null) return;

        // Lerp smooth the CPU/RAM values (prevents jarring per-second jumps)
        _smoothCpu = Lerp(_smoothCpu, (float)CpuUsage / 100f, 0.08f);
        _smoothRam = Lerp(_smoothRam, (float)RamUsage / 100f, 0.06f);

        _gl.Viewport(0, 0, (uint)Bounds.Width, (uint)Bounds.Height);
        _gl.ClearColor(0.04f, 0.04f, 0.06f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _gl.UseProgram(_program);

        _gl.Uniform1(_gl.GetUniformLocation(_program, "u_Time"),     (float)_clock.Elapsed.TotalSeconds);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "u_CpuUsage"), _smoothCpu);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "u_RamUsage"), _smoothRam);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Drives continuous 60 FPS animation
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _gl?.DeleteProgram(_program);
        _gl?.DeleteBuffer(_vbo);
        _gl?.DeleteVertexArray(_vao);
        base.OnOpenGlDeinit(gl);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private uint CreateShaderProgram(string vertSrc, string fragSrc)
    {
        if (_gl == null) return 0;

        var vert = CompileShader(ShaderType.VertexShader,   vertSrc, "Vertex");
        var frag = CompileShader(ShaderType.FragmentShader, fragSrc, "Fragment");
        if (vert == 0 || frag == 0) return 0;

        var prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vert);
        _gl.AttachShader(prog, frag);
        _gl.LinkProgram(prog);

        int linked = 0;
        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out linked);
        if (linked == 0)
        {
            Console.WriteLine($"[PulseGraphControl] GL Program Link Error: {_gl.GetProgramInfoLog(prog)}");
            _gl.DeleteProgram(prog);
            prog = 0;
        }

        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);
        return prog;
    }

    private uint CompileShader(ShaderType type, string src, string label)
    {
        if (_gl == null) return 0;
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, src);
        _gl.CompileShader(shader);

        int status = 0;
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out status);
        if (status == 0)
        {
            Console.WriteLine($"[PulseGraphControl] {label} Shader Error: {_gl.GetShaderInfoLog(shader)}");
            _gl.DeleteShader(shader);
            return 0;
        }
        return shader;
    }
}
