import * as THREE from "three";
import { TrackballControls } from "three/examples/jsm/controls/TrackballControls.js";
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer.js";
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass.js";
import { SSAOPass } from "three/examples/jsm/postprocessing/SSAOPass.js";
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass.js";
import { OutlinePass } from "three/examples/jsm/postprocessing/OutlinePass.js";
import { OutputPass } from "three/examples/jsm/postprocessing/OutputPass.js";
import { CapabilityDetector, IRenderBackend } from "./RenderBackend";
import { CacheManager, ElementMetadata } from "./CacheSystem";
import { DTGLBLoader } from "../loaders/GLBLoader";
import { DTParquetLoader } from "../loaders/ParquetLoader";
import { GuidResolver } from "../utils/GuidResolver";

export class DTEngine {
    private backend: IRenderBackend | null = null;
    private scene: THREE.Scene;
    private perspCamera: THREE.PerspectiveCamera;
    private orthoCamera: THREE.OrthographicCamera;
    private activeCamera: THREE.PerspectiveCamera | THREE.OrthographicCamera;
    private controls: TrackballControls | null = null;

    private composer: EffectComposer | null = null;
    private renderPass: RenderPass | null = null;
    private ssaoPass: SSAOPass | null = null;
    private bloomPass: UnrealBloomPass | null = null;
    private outlinePass: OutlinePass | null = null;

    private envMap: THREE.Texture | null = null;
    private _bgColor = new THREE.Color(0x0b1220);
    private _bgGradientCanvas: HTMLCanvasElement | null = null;
    private _bgGradientTexture: THREE.CanvasTexture | null = null;
    private _envBackgroundEnabled = false;

    private ambientLight: THREE.AmbientLight;
    private directionalLight: THREE.DirectionalLight;
    private fillLight: THREE.DirectionalLight;

    private gridHelper: THREE.GridHelper | null = null;
    private axesHelper: THREE.AxesHelper | null = null;

    private cacheManager = new CacheManager();
    private glbLoader: DTGLBLoader;
    private parquetLoader = new DTParquetLoader();
    private guidResolver: GuidResolver | null = null;

    private modelGroup: THREE.Group | null = null;
    private selectedGuid: string | null = null;
    private renderMode: "material" | "wireframe" | "xray" = "material";
    private _selectionOutlineEnabled = true;
    private _modelOutlineEnabled = false;

    private _cameraAnimStartPos = new THREE.Vector3();
    private _cameraAnimEndPos = new THREE.Vector3();
    private _cameraAnimStartTarget = new THREE.Vector3();
    private _cameraAnimEndTarget = new THREE.Vector3();
    private _cameraAnimStartUp = new THREE.Vector3(0, 1, 0);
    private _cameraAnimEndUp = new THREE.Vector3(0, 1, 0);
    private _cameraAnimLerpedUp = new THREE.Vector3(0, 1, 0);
    private _cameraAnimStartFov = 60;
    private _cameraAnimEndFov = 60;
    private _cameraAnimStartTime = 0;
    private _cameraAnimDuration = 0;
    private _cameraAnimDurationMs = 650;

    public onElementSelected?: (metadata: ElementMetadata | null, latency: number) => void;
    public onStatsUpdate?: (stats: any) => void;

    constructor() {
        this.scene = new THREE.Scene();
        this._updateBackgroundGradient();
        this.scene.background = this._bgGradientTexture;

        this.perspCamera = new THREE.PerspectiveCamera(60, 1, 0.1, 10000);
        this.perspCamera.position.set(50, 50, 50);

        this.orthoCamera = new THREE.OrthographicCamera(-50, 50, 50, -50, 0.1, 10000);
        this.orthoCamera.position.set(50, 50, 50);

        this.activeCamera = this.perspCamera;

        this.ambientLight = new THREE.AmbientLight(0xffffff, 0.3);
        this.scene.add(this.ambientLight);

        this.fillLight = new THREE.DirectionalLight(0xffffff, 0.9);
        this.fillLight.position.set(-30, 40, -20);
        this.fillLight.castShadow = false;
        this.scene.add(this.fillLight);

        this.directionalLight = new THREE.DirectionalLight(0xffffff, 1.7);
        this.directionalLight.castShadow = true;
        this.directionalLight.shadow.mapSize.width = 1024;
        this.directionalLight.shadow.mapSize.height = 1024;
        this.directionalLight.shadow.camera.near = 0.5;
        this.directionalLight.shadow.camera.far = 1000;
        this.directionalLight.shadow.camera.left = -150;
        this.directionalLight.shadow.camera.right = 150;
        this.directionalLight.shadow.camera.top = 150;
        this.directionalLight.shadow.camera.bottom = -150;
        this.directionalLight.shadow.bias = -0.0001;
        this.scene.add(this.directionalLight);

        this.setSunPosition(45, 60);

        this.glbLoader = new DTGLBLoader(this.cacheManager);
    }

    async initialize(canvas: HTMLCanvasElement): Promise<void> {
        this.backend = await CapabilityDetector.createBackend();
        await this.backend.initialize(canvas);

        const w = window.innerWidth;
        const h = window.innerHeight;
        this.backend.setSize(w, h);
        this.perspCamera.aspect = w / h;
        this.perspCamera.updateProjectionMatrix();

        await this.cacheManager.initialize();

        this.controls = new TrackballControls(this.activeCamera, canvas);
        this.controls.rotateSpeed = 2.2;
        this.controls.zoomSpeed = 1.2;
        this.controls.panSpeed = 1.0;
        this.controls.staticMoving = true;
        this.controls.dynamicDampingFactor = 0.12;
        this.controls.minDistance = 0.5;
        this.controls.maxDistance = 5000;
        this.controls.mouseButtons = {
            LEFT: THREE.MOUSE.ROTATE,
            MIDDLE: THREE.MOUSE.DOLLY,
            RIGHT: THREE.MOUSE.PAN,
        };

        this._generateEnvironmentMap();
        this._setupPostProcessing(w, h);
        this._setupHelpers();
        this._updateOutlineSelection(null);
        this.setExposure(0.9);
        this.setEnvironmentIntensity(0.7);
        this.setShadowsEnabled(true);
        this.setSSAOEnabled(false);

        window.addEventListener("resize", this._onResize);
    }

    async loadModel(glbUrl: string, parquetUrl: string): Promise<void> {
        this.modelGroup = await this.glbLoader.load(glbUrl);
        this.scene.add(this.modelGroup);

        const guidToMesh = new Map(
            this.glbLoader.getAllGuids().map((guid) => [guid, this.glbLoader.getMeshByGuid(guid)!])
        );
        const meshToGuid = new Map(
            Array.from(guidToMesh.entries()).map(([guid, mesh]) => [mesh, guid])
        );
        this.guidResolver = new GuidResolver(guidToMesh, meshToGuid);

        try {
            await this.parquetLoader.initialize(parquetUrl);
            this.cacheManager.setParquetLoader(this.parquetLoader);
        } catch {
            console.warn("Parquet unavailable, GLB-only mode");
        }

        this.fitToView();
    }

    render(): void {
        if (!this.backend) return;

        if (this._cameraAnimStartTime > 0) {
            const t = Math.min(
                (performance.now() - this._cameraAnimStartTime) / this._cameraAnimDuration,
                1
            );
            const eased = 1 - Math.pow(1 - t, 3);
            this.activeCamera.position.lerpVectors(
                this._cameraAnimStartPos,
                this._cameraAnimEndPos,
                eased
            );
            if (this.controls) {
                this.controls.target.lerpVectors(
                    this._cameraAnimStartTarget,
                    this._cameraAnimEndTarget,
                    eased
                );
                this._cameraAnimLerpedUp.lerpVectors(
                    this._cameraAnimStartUp,
                    this._cameraAnimEndUp,
                    eased
                );
                this.activeCamera.up.copy(this._cameraAnimLerpedUp).normalize();
                this.activeCamera.lookAt(this.controls.target);
            }
            this.perspCamera.fov =
                this._cameraAnimStartFov +
                (this._cameraAnimEndFov - this._cameraAnimStartFov) * eased;
            this.perspCamera.updateProjectionMatrix();
            if (t >= 1) {
                this._cameraAnimStartTime = 0;
            }
        }

        this.controls?.update();

        if (this.composer) {
            this.composer.render();
        } else {
            this.backend.render(this.scene, this.activeCamera);
        }

        if (this.onStatsUpdate) {
            this.onStatsUpdate({
                backend: this.backend.type,
                triangles: this.backend.renderer.info.render.triangles,
                calls: this.backend.renderer.info.render.calls,
                cacheSize: this.cacheManager.getStats().l1Size,
            });
        }
    }

    async handleClick(clientX: number, clientY: number): Promise<void> {
        if (!this.guidResolver) return;

        const canvas = this.backend!.renderer.domElement;
        const rect = canvas.getBoundingClientRect();
        const ndcX = ((clientX - rect.left) / rect.width) * 2 - 1;
        const ndcY = -((clientY - rect.top) / rect.height) * 2 + 1;

        const guid = this.guidResolver.pick(ndcX, ndcY, this.activeCamera, this.scene);

        if (guid) {
            if (this.selectedGuid) {
                this.guidResolver.clearHighlight(this.selectedGuid);
            }
            this.guidResolver.highlightByGuid(guid);
            this.selectedGuid = guid;

            this._updateOutlineSelection(guid);

            const result = await this.cacheManager.lookup(guid);
            this.onElementSelected?.(result.data, result.latency);
        } else {
            if (this.selectedGuid) {
                this.guidResolver.clearHighlight(this.selectedGuid);
                this.selectedGuid = null;
            }
            this._updateOutlineSelection(null);
            this.onElementSelected?.(null, 0);
        }
    }

    // --- Environment ---

    setBackgroundColor(hex: string): void {
        this._bgColor.set(hex);
        if (!this._envBackgroundEnabled) {
            this._updateBackgroundGradient();
            this.scene.background = this._bgGradientTexture;
        }
    }

    setEnvironmentMapEnabled(enabled: boolean): void {
        this._envBackgroundEnabled = enabled;
        if (enabled && this.envMap) {
            this.scene.background = this.envMap;
        } else {
            this.scene.background = this._bgGradientTexture;
        }
    }

    setEnvironmentIntensity(intensity: number): void {
        (this.scene as any).environmentIntensity = intensity;
    }

    setEnvironmentBlur(blur: number): void {
        this.scene.backgroundBlurriness = blur;
    }

    // --- Lighting ---

    setExposure(exposure: number): void {
        if (this.backend) {
            this.backend.renderer.toneMappingExposure = exposure;
        }
    }

    setAmbientIntensity(intensity: number): void {
        this.ambientLight.intensity = intensity;
    }

    setDirectionalIntensity(intensity: number): void {
        this.directionalLight.intensity = intensity;
    }

    setSunPosition(azimuthDeg: number, elevationDeg: number): void {
        const az = THREE.MathUtils.degToRad(azimuthDeg);
        const el = THREE.MathUtils.degToRad(elevationDeg);
        const r = 100;
        this.directionalLight.position.set(
            r * Math.cos(el) * Math.sin(az),
            r * Math.sin(el),
            r * Math.cos(el) * Math.cos(az)
        );
    }

    setShadowsEnabled(enabled: boolean): void {
        this.backend!.renderer.shadowMap.enabled = enabled;
        this.directionalLight.castShadow = enabled;
        this.backend!.renderer.shadowMap.needsUpdate = true;
    }

    // --- Post-Processing ---

    setSSAOEnabled(enabled: boolean): void {
        if (this.ssaoPass) this.ssaoPass.enabled = enabled;
    }

    setSSAORadius(radius: number): void {
        if (this.ssaoPass) this.ssaoPass.kernelRadius = radius;
    }

    setBloomEnabled(enabled: boolean): void {
        if (this.bloomPass) this.bloomPass.enabled = enabled;
    }

    setBloomStrength(strength: number): void {
        if (this.bloomPass) this.bloomPass.strength = strength;
    }

    setBloomThreshold(threshold: number): void {
        if (this.bloomPass) this.bloomPass.threshold = threshold;
    }

    setOutlineEnabled(enabled: boolean): void {
        this._selectionOutlineEnabled = enabled;
        this._updateOutlineSelection(this.selectedGuid);
    }

    setModelOutlineEnabled(enabled: boolean): void {
        this._modelOutlineEnabled = enabled;
        this._updateOutlineSelection(this.selectedGuid);
    }

    // --- Camera ---

    setFOV(fov: number): void {
        this.perspCamera.fov = fov;
        this.perspCamera.updateProjectionMatrix();
    }

    setProjection(mode: "perspective" | "orthographic"): void {
        const target = this.controls!.target.clone();
        const position = this.activeCamera.position.clone();

        if (mode === "orthographic") {
            const distance = position.distanceTo(target);
            const halfH = Math.tan(THREE.MathUtils.degToRad(this.perspCamera.fov / 2)) * distance;
            const halfW = halfH * this.perspCamera.aspect;
            this.orthoCamera.left = -halfW;
            this.orthoCamera.right = halfW;
            this.orthoCamera.top = halfH;
            this.orthoCamera.bottom = -halfH;
            this.orthoCamera.near = 0.1;
            this.orthoCamera.far = 10000;
            this.orthoCamera.position.copy(position);
            this.orthoCamera.quaternion.copy(this.perspCamera.quaternion);
            this.orthoCamera.updateProjectionMatrix();
            this.activeCamera = this.orthoCamera;
        } else {
            this.perspCamera.position.copy(position);
            this.perspCamera.quaternion.copy(this.orthoCamera.quaternion);
            this.perspCamera.updateProjectionMatrix();
            this.activeCamera = this.perspCamera;
        }

        (this.controls as any).object = this.activeCamera;
        this.controls!.target.copy(target);
        this.controls!.update();

        if (this.renderPass) this.renderPass.camera = this.activeCamera;
        if (this.ssaoPass) (this.ssaoPass as any).camera = this.activeCamera;
        if (this.outlinePass) this.outlinePass.renderCamera = this.activeCamera;
    }

    // --- Display ---

    setRenderMode(mode: "material" | "wireframe" | "xray"): void {
        this.renderMode = mode;
        this.scene.traverse((node) => {
            if (!(node instanceof THREE.Mesh)) return;
            const mat = node.material as THREE.Material & {
                wireframe?: boolean;
                transparent?: boolean;
                opacity?: number;
            };
            switch (mode) {
                case "material":
                    if ((node as any)._originalMaterial) {
                        node.material = (node as any)._originalMaterial;
                    }
                    mat.wireframe = false;
                    mat.transparent = false;
                    mat.opacity = 1.0;
                    break;
                case "wireframe":
                    mat.wireframe = true;
                    break;
                case "xray":
                    mat.wireframe = false;
                    mat.transparent = true;
                    mat.opacity = 0.3;
                    break;
            }
            mat.needsUpdate = true;
        });
    }

    setGridVisible(visible: boolean): void {
        if (this.gridHelper) this.gridHelper.visible = visible;
    }

    setAxesVisible(visible: boolean): void {
        if (this.axesHelper) this.axesHelper.visible = visible;
    }

    resetCamera(): void {
        this.perspCamera.position.set(50, 50, 50);
        this.perspCamera.lookAt(0, 0, 0);
        this.controls?.reset();
    }

    animateResetView(): void {
        if (!this.controls) return;
        this._cameraAnimStartPos.copy(this.activeCamera.position);
        this._cameraAnimEndPos.set(50, 50, 50);
        this._cameraAnimStartTarget.copy(this.controls.target);
        this._cameraAnimEndTarget.set(0, 0, 0);
        this._cameraAnimStartUp.copy(this.activeCamera.up);
        this._cameraAnimEndUp.set(0, 1, 0);
        this._cameraAnimStartFov = this.perspCamera.fov;
        this._cameraAnimEndFov = 60;
        this._cameraAnimStartTime = performance.now();
        this._cameraAnimDuration = this._cameraAnimDurationMs;
    }

    setCameraAnimDurationMs(ms: number): void {
        this._cameraAnimDurationMs = ms;
    }

    animateCameraTo(
        eye: THREE.Vector3,
        target: THREE.Vector3,
        up: THREE.Vector3,
        fov: number
    ): void {
        if (!this.controls) return;
        this._cameraAnimStartPos.copy(this.activeCamera.position);
        this._cameraAnimEndPos.copy(eye);
        this._cameraAnimStartTarget.copy(this.controls.target);
        this._cameraAnimEndTarget.copy(target);
        this._cameraAnimStartUp.copy(this.activeCamera.up);
        this._cameraAnimEndUp.copy(up);
        this._cameraAnimStartFov = this.perspCamera.fov;
        this._cameraAnimEndFov = fov;
        this._cameraAnimStartTime = performance.now();
        this._cameraAnimDuration = this._cameraAnimDurationMs;
    }

    getCameraState(): {
        FIELD_OF_VIEW: number;
        WORLD_UP_VECTOR: [number, number, number];
        EYE: [number, number, number];
        TARGET: [number, number, number];
        UP: [number, number, number];
    } {
        const pos = this.activeCamera.position;
        const tgt = this.controls?.target ?? new THREE.Vector3(0, 0, 0);
        const u = this.activeCamera.up;
        return {
            FIELD_OF_VIEW: this.perspCamera.fov,
            WORLD_UP_VECTOR: [0, 1, 0],
            EYE: [pos.x, pos.y, pos.z],
            TARGET: [tgt.x, tgt.y, tgt.z],
            UP: [u.x, u.y, u.z],
        };
    }

    animateFitToView(): void {
        if (!this.modelGroup || !this.controls) return;

        const box = new THREE.Box3().setFromObject(this.modelGroup);
        const center = box.getCenter(new THREE.Vector3());
        const size = box.getSize(new THREE.Vector3());
        const maxDim = Math.max(size.x, size.y, size.z);
        const fov = this.perspCamera.fov * (Math.PI / 180);
        const dist = Math.abs(maxDim / 2 / Math.tan(fov / 2)) * 1.5;
        const endPos = new THREE.Vector3(
            center.x + dist * 0.6,
            center.y + dist * 0.5,
            center.z + dist * 0.6
        );

        this._cameraAnimStartPos.copy(this.activeCamera.position);
        this._cameraAnimEndPos.copy(endPos);
        this._cameraAnimStartTarget.copy(this.controls.target);
        this._cameraAnimEndTarget.copy(center);
        this._cameraAnimStartUp.copy(this.activeCamera.up);
        this._cameraAnimEndUp.set(0, 1, 0);
        this._cameraAnimStartFov = this.perspCamera.fov;
        this._cameraAnimEndFov = this.perspCamera.fov;
        this._cameraAnimStartTime = performance.now();
        this._cameraAnimDuration = this._cameraAnimDurationMs;

        if (this.gridHelper) {
            const wasVisible = this.gridHelper.visible;
            const gridSize = Math.ceil(maxDim * 2);
            this.scene.remove(this.gridHelper);
            this.gridHelper = new THREE.GridHelper(gridSize, gridSize / 2, 0x333333, 0x1a1a1a);
            this.gridHelper.position.y = box.min.y;
            this.gridHelper.visible = wasVisible;
            this.scene.add(this.gridHelper);
        }
    }

    fitToView(): void {
        if (!this.modelGroup) return;

        const box = new THREE.Box3().setFromObject(this.modelGroup);
        const center = box.getCenter(new THREE.Vector3());
        const size = box.getSize(new THREE.Vector3());
        const maxDim = Math.max(size.x, size.y, size.z);
        const fov = this.perspCamera.fov * (Math.PI / 180);
        const dist = Math.abs(maxDim / 2 / Math.tan(fov / 2)) * 1.5;

        this.activeCamera.position.set(
            center.x + dist * 0.6,
            center.y + dist * 0.5,
            center.z + dist * 0.6
        );
        this.activeCamera.lookAt(center);

        if (this.controls) {
            this.controls.target.copy(center);
            this.controls.update();
        }

        if (this.gridHelper) {
            const wasVisible = this.gridHelper.visible;
            const gridSize = Math.ceil(maxDim * 2);
            this.scene.remove(this.gridHelper);
            this.gridHelper = new THREE.GridHelper(gridSize, gridSize / 2, 0x333333, 0x1a1a1a);
            this.gridHelper.position.y = box.min.y;
            this.gridHelper.visible = wasVisible;
            this.scene.add(this.gridHelper);
        }
    }

    dispose(): void {
        this.backend?.dispose();
        this.parquetLoader?.dispose();
        this.controls?.dispose();
        this.composer?.dispose();
        this.envMap?.dispose();
        window.removeEventListener("resize", this._onResize);
    }

    // --- Private ---

    private _generateEnvironmentMap(): void {
        const renderer = this.backend!.renderer;
        const pmrem = new THREE.PMREMGenerator(renderer);

        const envScene = new THREE.Scene();
        const geo = new THREE.SphereGeometry(500, 64, 32);
        const mat = new THREE.ShaderMaterial({
            vertexShader: `
                varying vec3 vWorldPos;
                void main() {
                    vWorldPos = (modelMatrix * vec4(position, 1.0)).xyz;
                    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
                }
            `,
            fragmentShader: `
                varying vec3 vWorldPos;
                void main() {
                    vec3 d = normalize(vWorldPos);
                    float y = d.y;
                    vec3 skyTop = vec3(0.32, 0.52, 0.82);
                    vec3 skyHrz = vec3(0.72, 0.82, 0.92);
                    vec3 gndHrz = vec3(0.82, 0.80, 0.75);
                    vec3 gndBot = vec3(0.52, 0.50, 0.45);
                    vec3 c = y > 0.0
                        ? mix(skyHrz, skyTop, pow(y, 0.45))
                        : mix(gndHrz, gndBot, pow(-y, 0.7));
                    vec3 sun = normalize(vec3(0.5, 0.4, 0.5));
                    float sd = max(dot(d, sun), 0.0);
                    c += vec3(1.0, 0.95, 0.85) * pow(sd, 128.0) * 2.5;
                    c += vec3(0.25, 0.18, 0.08) * pow(sd, 8.0) * 0.5;
                    gl_FragColor = vec4(c, 1.0);
                }
            `,
            side: THREE.BackSide,
        });

        envScene.add(new THREE.Mesh(geo, mat));
        this.envMap = pmrem.fromScene(envScene, 0, 0.1, 1000).texture;

        this.scene.environment = this.envMap;
        (this.scene as any).environmentIntensity = 1.0;

        pmrem.dispose();
        geo.dispose();
        mat.dispose();
    }

    private _setupPostProcessing(width: number, height: number): void {
        const renderer = this.backend!.renderer;
        this.composer = new EffectComposer(renderer);
        this.composer.setPixelRatio(Math.min(1.25, window.devicePixelRatio));

        this.renderPass = new RenderPass(this.scene, this.activeCamera);
        this.composer.addPass(this.renderPass);

        this.ssaoPass = new SSAOPass(this.scene, this.activeCamera, width, height);
        this.ssaoPass.kernelRadius = 6;
        this.ssaoPass.minDistance = 0.02;
        this.ssaoPass.maxDistance = 0.15;
        this.ssaoPass.enabled = false;
        this.composer.addPass(this.ssaoPass);

        this.bloomPass = new UnrealBloomPass(new THREE.Vector2(width, height), 0.5, 0.4, 0.85);
        this.bloomPass.enabled = false;
        this.composer.addPass(this.bloomPass);

        this.outlinePass = new OutlinePass(
            new THREE.Vector2(width, height),
            this.scene,
            this.activeCamera
        );
        this.outlinePass.pulsePeriod = 0;
        this.composer.addPass(this.outlinePass);

        const outputPass = new OutputPass();
        this.composer.addPass(outputPass);
    }

    private _setupHelpers(): void {
        this.gridHelper = new THREE.GridHelper(200, 100, 0x333333, 0x1a1a1a);
        this.gridHelper.visible = false;
        this.scene.add(this.gridHelper);

        this.axesHelper = new THREE.AxesHelper(10);
        this.scene.add(this.axesHelper);
    }

    private _updateOutlineSelection(guid: string | null): void {
        if (!this.outlinePass) return;
        const meshes: THREE.Object3D[] = [];
        if (this._modelOutlineEnabled && this.modelGroup) {
            this.modelGroup.traverse((n) => {
                if (n instanceof THREE.Mesh) meshes.push(n);
            });
        }
        if (this._selectionOutlineEnabled && guid) {
            const obj = this.glbLoader.getMeshByGuid(guid);
            if (obj) {
                obj.traverse((n) => {
                    if (n instanceof THREE.Mesh) meshes.push(n);
                });
            }
        }
        this.outlinePass.selectedObjects = meshes;
        this.outlinePass.enabled = meshes.length > 0;
        if (this._modelOutlineEnabled) {
            this.outlinePass.edgeStrength = 1.2;
            this.outlinePass.edgeGlow = 0;
            this.outlinePass.edgeThickness = 1;
            this.outlinePass.visibleEdgeColor.set("#000000");
            this.outlinePass.hiddenEdgeColor.set("#000000");
        } else {
            this.outlinePass.edgeStrength = 3;
            this.outlinePass.edgeGlow = 0.4;
            this.outlinePass.edgeThickness = 1.5;
            this.outlinePass.visibleEdgeColor.set("#58a6ff");
            this.outlinePass.hiddenEdgeColor.set("#1f3d6f");
        }
    }

    private _updateBackgroundGradient(): void {
        const canvas = this._bgGradientCanvas ?? document.createElement("canvas");
        canvas.width = 2;
        canvas.height = 256;
        const ctx = canvas.getContext("2d");
        if (!ctx) return;

        const top = this._bgColor.clone();
        const bottom = new THREE.Color(0xffffff);
        const gradient = ctx.createLinearGradient(0, 0, 0, canvas.height);
        gradient.addColorStop(0, `#${top.getHexString()}`);
        gradient.addColorStop(1, `#${bottom.getHexString()}`);
        ctx.fillStyle = gradient;
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        if (!this._bgGradientTexture) {
            this._bgGradientTexture = new THREE.CanvasTexture(canvas);
            this._bgGradientTexture.colorSpace = THREE.SRGBColorSpace;
        }
        this._bgGradientTexture.needsUpdate = true;
        this._bgGradientCanvas = canvas;
    }

    private _onResize = (): void => {
        const w = window.innerWidth;
        const h = window.innerHeight;

        this.perspCamera.aspect = w / h;
        this.perspCamera.updateProjectionMatrix();

        if (this.activeCamera === this.orthoCamera) {
            const halfH = (this.orthoCamera.top - this.orthoCamera.bottom) / 2;
            const halfW = halfH * (w / h);
            this.orthoCamera.left = -halfW;
            this.orthoCamera.right = halfW;
            this.orthoCamera.updateProjectionMatrix();
        }

        this.backend?.setSize(w, h);
        this.composer?.setSize(w, h);
        this.controls?.handleResize();
    };
}
