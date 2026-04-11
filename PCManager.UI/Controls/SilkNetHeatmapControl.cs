using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace PCManager.UI.Controls;

public class SilkNetHeatmapControl : OpenGlControlBase
{
    private GL? _gl;
    private uint _program;
    private uint _vbo;
    private uint _vao;
    private Stopwatch _st = Stopwatch.StartNew();

    public static readonly StyledProperty<double> CpuUsageProperty =
        AvaloniaProperty.Register<SilkNetHeatmapControl, double>(nameof(CpuUsage));

    public double CpuUsage
    {
        get => GetValue(CpuUsageProperty);
        set => SetValue(CpuUsageProperty, value);
    }

    public static readonly StyledProperty<double> RamUsageProperty =
        AvaloniaProperty.Register<SilkNetHeatmapControl, double>(nameof(RamUsage));

    public double RamUsage
    {
        get => GetValue(RamUsageProperty);
        set => SetValue(RamUsageProperty, value);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _gl = GL.GetApi(gl.GetProcAddress);

        // Vertex Data: Full-screen quad
        float[] vertices = {
            -1.0f, -1.0f,
             1.0f, -1.0f,
            -1.0f,  1.0f,
             1.0f,  1.0f,
        };

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);

        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, (void*)0);
        }
        _gl.EnableVertexAttribArray(0);

        // Shaders
        var vShader = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(vShader, @"
            #version 330 core
            layout (location = 0) in vec2 aPos;
            out vec2 TexCoord;
            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                TexCoord = aPos * 0.5 + 0.5;
            }");
        _gl.CompileShader(vShader);
        CheckShader(vShader, "Vertex");

        var fShader = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(fShader, @"
            #version 330 core
            in vec2 TexCoord;
            out vec4 FragColor;
            uniform float time;
            uniform float cpuUsage;
            uniform float ramUsage;

            void main() {
                vec2 uv = TexCoord;
                
                // Grid warping
                float warp = sin(uv.y * 10.0 + time * 2.0) * 0.01 * (cpuUsage / 100.0);
                uv.x += warp;

                // Pulsing line (Sine wave)
                float freq = 10.0 + (cpuUsage * 0.2);
                float amp = 0.1 + (cpuUsage * 0.002);
                float lineY = 0.5 + sin(uv.x * freq + time * 5.0) * amp;
                
                float dist = abs(uv.y - lineY);
                float edge = 0.02;
                float brightness = smoothstep(edge, 0.0, dist);

                // Bloom effect / Glow
                vec3 lineColor = vec3(0.0, 0.9, 1.0); // Cyan
                if (cpuUsage > 70.0) lineColor = vec3(1.0, 0.2, 0.0); // Red alert
                
                vec3 color = brightness * lineColor * 2.0;
                color += (1.0 - smoothstep(0.0, 0.15, dist)) * lineColor * 0.5; // Outer glow

                // Grid lines
                float gridX = step(0.98, fract(uv.x * 20.0));
                float gridY = step(0.98, fract(uv.y * 10.0));
                vec3 gridColor = vec3(0.1, 0.1, 0.2) * (gridX + gridY);
                
                color += gridColor;

                FragColor = vec4(color, 1.0);
            }");
        _gl.CompileShader(fShader);
        CheckShader(fShader, "Fragment");

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vShader);
        _gl.AttachShader(_program, fShader);
        _gl.LinkProgram(_program);
        CheckProgram(_program);

        _gl.DeleteShader(vShader);
        _gl.DeleteShader(fShader);
    }

    private unsafe void CheckShader(uint shader, string type)
    {
        int status = 0;
        _gl?.GetShader(shader, ShaderParameterName.CompileStatus, out status);
        if (status == 0)
        {
            var log = _gl?.GetShaderInfoLog(shader);
            Console.WriteLine($"GL Error ({type} Shader): {log}");
        }
    }

    private unsafe void CheckProgram(uint program)
    {
        int status = 0;
        _gl?.GetProgram(program, ProgramPropertyARB.LinkStatus, out status);
        if (status == 0)
        {
            var log = _gl?.GetProgramInfoLog(program);
            Console.WriteLine($"GL Error (Program Link): {log}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _gl?.DeleteProgram(_program);
        _gl?.DeleteBuffer(_vbo);
        _gl?.DeleteVertexArray(_vao);
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl == null) return;

        _gl.ClearColor(0.05f, 0.05f, 0.07f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _gl.UseProgram(_program);
        
        int timeLoc = _gl.GetUniformLocation(_program, "time");
        _gl.Uniform1(timeLoc, (float)_st.Elapsed.TotalSeconds);

        int cpuLoc = _gl.GetUniformLocation(_program, "cpuUsage");
        _gl.Uniform1(cpuLoc, (float)CpuUsage);

        int ramLoc = _gl.GetUniformLocation(_program, "ramUsage");
        _gl.Uniform1(ramLoc, (float)RamUsage);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        RequestNextFrameRendering();
    }
}
