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
    public get renderer(): THREE.WebGLRenderer {
        return this._renderer;
    }
    public get capabilities(): BackendCapabilities {
        return this._capabilities;
    }
    private _renderer!: THREE.WebGLRenderer;
    private _capabilities!: BackendCapabilities;

    private idRenderTarget: THREE.WebGLRenderTarget | null = null;

    async initialize(canvas: HTMLCanvasElement): Promise<void> {
        this._renderer = new THREE.WebGLRenderer({
            canvas,
            antialias: true,
            alpha: false,
            powerPreference: "high-performance",
        });

        this._renderer.shadowMap.enabled = true;
        this._renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        this._renderer.toneMapping = THREE.NoToneMapping;
        this._renderer.toneMappingExposure = 1.0;
        this._renderer.outputColorSpace = THREE.SRGBColorSpace;
        this._renderer.setPixelRatio(Math.min(2.0, window.devicePixelRatio));
        this._renderer.setSize(canvas.clientWidth, canvas.clientHeight);

        const gl = this._renderer.getContext() as WebGL2RenderingContext;
        this._capabilities = {
            computeShaders: false,
            indirectDraw: gl.getExtension("WEBGL_multi_draw") !== null,
            maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
            maxBufferSize: gl.getParameter(gl.MAX_UNIFORM_BLOCK_SIZE),
            multiDrawIndirect: false,
            gpuDrivenCulling: false,
        };

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
        this._renderer.dispose();
    }

    render(scene: THREE.Scene, camera: THREE.Camera): void {
        this._renderer.render(scene, camera);
    }

    setSize(width: number, height: number): void {
        this._renderer.setSize(width, height);
        this.idRenderTarget?.setSize(width, height);
    }

    readPixelId(x: number, y: number): number {
        const pixelBuffer = new Uint8Array(4);
        this._renderer.readRenderTargetPixels(
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
