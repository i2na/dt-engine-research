import * as THREE from "three";

/**
 * Rendering Abstraction Layer (RAL)
 * Dual backend: WebGPU (preferred) with WebGL 2.0 fallback
 */

export type BackendType = "webgpu" | "webgl2";

export interface BackendCapabilities {
    computeShaders: boolean;
    indirectDraw: boolean;
    maxTextureSize: number;
    maxBufferSize: number;
    multiDrawIndirect: boolean;
    gpuDrivenCulling: boolean;
}

export interface IRenderBackend {
    readonly type: BackendType;
    readonly capabilities: BackendCapabilities;
    readonly renderer: THREE.WebGLRenderer;

    initialize(canvas: HTMLCanvasElement): Promise<void>;
    dispose(): void;
    render(scene: THREE.Scene, camera: THREE.Camera): void;
    setSize(width: number, height: number): void;
    readPixelId(x: number, y: number): number;
}

/**
 * WebGL 2.0 Backend
 * Universal fallback with maximum compatibility
 */
export class WebGL2Backend implements IRenderBackend {
    public readonly type: BackendType = "webgl2";
    public readonly renderer: THREE.WebGLRenderer;
    public readonly capabilities: BackendCapabilities;

    private idRenderTarget: THREE.WebGLRenderTarget | null = null;
    private idMaterial: THREE.ShaderMaterial | null = null;

    constructor() {
        // Create WebGL 2.0 renderer
        this.renderer = new THREE.WebGLRenderer({
            antialias: true,
            alpha: false,
            powerPreference: "high-performance",
        });

        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
        this.renderer.toneMappingExposure = 1.0;

        // Detect capabilities
        const gl = this.renderer.getContext() as WebGL2RenderingContext;
        this.capabilities = {
            computeShaders: false,
            indirectDraw: gl.getExtension("WEBGL_multi_draw") !== null,
            maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
            maxBufferSize: gl.getParameter(gl.MAX_UNIFORM_BLOCK_SIZE),
            multiDrawIndirect: false,
            gpuDrivenCulling: false,
        };
    }

    async initialize(canvas: HTMLCanvasElement): Promise<void> {
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(canvas.clientWidth, canvas.clientHeight);
        canvas.appendChild(this.renderer.domElement);

        // Create ID render target for object picking (MRT simulation)
        this.idRenderTarget = new THREE.WebGLRenderTarget(canvas.clientWidth, canvas.clientHeight, {
            minFilter: THREE.NearestFilter,
            magFilter: THREE.NearestFilter,
            format: THREE.RGBAFormat,
            type: THREE.UnsignedByteType,
        });

        console.log("✓ WebGL 2.0 backend initialized");
        console.log("Capabilities:", this.capabilities);
    }

    dispose(): void {
        this.idRenderTarget?.dispose();
        this.renderer.dispose();
    }

    render(scene: THREE.Scene, camera: THREE.Camera): void {
        this.renderer.render(scene, camera);
    }

    setSize(width: number, height: number): void {
        this.renderer.setSize(width, height);
        this.idRenderTarget?.setSize(width, height);
    }

    readPixelId(x: number, y: number): number {
        // Render ID buffer and read pixel
        // This is a simplified implementation
        // Full implementation would render scene with ID shader
        const pixelBuffer = new Uint8Array(4);
        this.renderer.readRenderTargetPixels(
            this.idRenderTarget!,
            x,
            this.idRenderTarget!.height - y,
            1,
            1,
            pixelBuffer
        );

        // Decode RGB to object ID
        return (pixelBuffer[0] << 16) | (pixelBuffer[1] << 8) | pixelBuffer[2];
    }
}

/**
 * Capability Detection
 * Determines the best available rendering backend
 */
export class CapabilityDetector {
    static async detectBestBackend(): Promise<BackendType> {
        // Check WebGPU support
        if ("gpu" in navigator) {
            try {
                const adapter = await (navigator as any).gpu.requestAdapter();
                if (adapter) {
                    console.log("✓ WebGPU available");
                    // For now, we'll use WebGL 2.0 as Three.js WebGPU is still experimental
                    // return 'webgpu';
                }
            } catch (e) {
                console.log("✗ WebGPU not available:", e);
            }
        }

        // Fallback to WebGL 2.0
        console.log("→ Using WebGL 2.0 backend");
        return "webgl2";
    }

    static async createBackend(): Promise<IRenderBackend> {
        const type = await this.detectBestBackend();

        switch (type) {
            case "webgl2":
                return new WebGL2Backend();
            default:
                return new WebGL2Backend();
        }
    }
}
