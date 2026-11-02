// Reconstruction of the wasm-bindgen glue for libzproto_wasm_bg.*.wasm.
//
// Source of truth: Zalo PC 26.10.20, pc-dist/shared-worker.*.js, webpack module "Vox7"
// (glue) and the "sync2-proto" wrapper class (createMetaData / decryptMetaData / ...).
// The wasm binary is NOT bundled here; pass the path of the copy that ships inside
// /Applications/Zalo.app (or the extracted copy under /tmp/zalo-re).
//
// Everything below is a faithful, typed port of the glue; names of the wasm
// exports are exact, argument roles are inferred from the call sites (see SPEC.md).

import { readFileSync } from "node:fs";

export interface KeyPair {
  publicKey: Uint8Array;
  privateKey: Uint8Array;
}
export interface MetadataEncrypted {
  sessionId: string;
  ciphertext: Uint8Array;
  receiverIdentity: Uint8Array;
  sessionRecord: Uint8Array;
}
export interface MetadataDecrypted {
  sessionId: string;
  plaintext: Uint8Array;
  senderIdentity: Uint8Array;
  sessionRecord: Uint8Array;
}
export interface MessageEncrypted {
  sessionId: string;
  ciphertext: Uint8Array;
}
export interface MessageDecrypted {
  sessionId: string;
  plaintext: Uint8Array;
}

export class Zproto {
  private wasm!: Record<string, any>;
  private heap: any[] = new Array(128).fill(undefined);
  private heapNext = 0;
  private cachedU8: Uint8Array | null = null;
  private cachedDV: DataView | null = null;
  private vecLen = 0;
  private textDec = new TextDecoder("utf-8", { ignoreBOM: true, fatal: true });
  private textEnc = new TextEncoder();

  private constructor() {
    this.heap.push(undefined, null, true, false);
    this.heapNext = this.heap.length;
  }

  /** @param wasm path to libzproto_wasm_bg.*.wasm, or its bytes (e.g. from readWasmFromAsar) */
  static async load(wasm: string | Uint8Array): Promise<Zproto> {
    const z = new Zproto();
    const bytes = typeof wasm === "string" ? readFileSync(wasm) : wasm;
    const { instance } = await WebAssembly.instantiate(bytes, z.imports());
    z.wasm = instance.exports as Record<string, any>;
    return z;
  }

  // ---- heap object table (wasm-bindgen externref emulation) ----------------
  private addHeap(v: any): number {
    if (this.heapNext === this.heap.length) this.heap.push(this.heap.length + 1);
    const idx = this.heapNext;
    this.heapNext = this.heap[idx];
    this.heap[idx] = v;
    return idx;
  }
  private getHeap(i: number): any {
    return this.heap[i];
  }
  private dropHeap(i: number) {
    if (i < 132) return;
    this.heap[i] = this.heapNext;
    this.heapNext = i;
  }
  private takeHeap(i: number): any {
    const v = this.getHeap(i);
    this.dropHeap(i);
    return v;
  }

  // ---- memory helpers ------------------------------------------------------
  private u8(): Uint8Array {
    const buf = this.wasm.memory.buffer as ArrayBuffer;
    if (!this.cachedU8 || this.cachedU8.buffer !== buf) this.cachedU8 = new Uint8Array(buf);
    return this.cachedU8;
  }
  private dv(): DataView {
    const buf = this.wasm.memory.buffer as ArrayBuffer;
    if (!this.cachedDV || this.cachedDV.buffer !== buf) this.cachedDV = new DataView(buf);
    return this.cachedDV;
  }
  private str(ptr: number, len: number): string {
    ptr >>>= 0;
    return this.textDec.decode(this.u8().subarray(ptr, ptr + len));
  }
  private passBytes(b: Uint8Array): number {
    const ptr = (this.wasm.__wbindgen_export_2(b.length, 1) as number) >>> 0;
    this.u8().set(b, ptr);
    this.vecLen = b.length;
    return ptr;
  }
  private passStr(s: string): number {
    const enc = this.textEnc.encode(s);
    const ptr = (this.wasm.__wbindgen_export_2(enc.length, 1) as number) >>> 0;
    this.u8().subarray(ptr, ptr + enc.length).set(enc);
    this.vecLen = enc.length;
    return ptr;
  }

  // ---- imports (exact set required by the module) --------------------------
  private imports(): WebAssembly.Imports {
    const self_ = this;
    const wbg: Record<string, Function> = {
      __wbindgen_object_drop_ref: (i: number) => void self_.takeHeap(i),
      __wbg_new_405e22f390576ce2: () => self_.addHeap(new Object()),
      __wbg_set_3f1d0b984ed272ed: (o: number, k: number, v: number) => {
        self_.getHeap(o)[self_.takeHeap(k)] = self_.takeHeap(v);
      },
      __wbindgen_string_new: (p: number, l: number) => self_.addHeap(self_.str(p, l)),
      __wbindgen_number_new: (n: number) => self_.addHeap(n),
      __wbg_new_78feb108b6472713: () => self_.addHeap(new Array()),
      __wbg_push_737cfc8c1432c2c6: (a: number, v: number) => self_.getHeap(a).push(self_.getHeap(v)),
      __wbg_apply_36be6a55257c99bf: (f: number, t: number, a: number) =>
        self_.guard(() => self_.addHeap(self_.getHeap(f).apply(self_.getHeap(t), self_.getHeap(a)))),
      __wbindgen_memory: () => self_.addHeap(self_.wasm.memory),
      __wbg_buffer_609cc3eee51ed158: (i: number) => self_.addHeap(self_.getHeap(i).buffer),
      __wbg_newwithbyteoffsetandlength_d97e637ebe145a9a: (b: number, off: number, len: number) =>
        self_.addHeap(new Uint8Array(self_.getHeap(b), off >>> 0, len >>> 0)),
      __wbg_new_a12002a7f91c75be: (i: number) => self_.addHeap(new Uint8Array(self_.getHeap(i))),
      __wbg_new_8a6f238a6ece86ea: () => self_.addHeap(new Error()),
      __wbg_stack_0ed75d68575b0f3c: (retptr: number, errIdx: number) => {
        const p = self_.passStr(String(self_.getHeap(errIdx).stack));
        self_.dv().setInt32(retptr + 4, self_.vecLen, true);
        self_.dv().setInt32(retptr + 0, p, true);
      },
      __wbg_error_7534b8e9a36f1ab4: (p: number, l: number) => {
        // glue frees the string it was handed; we only log nothing (no logging enabled)
        self_.wasm.__wbindgen_export_1(p, l, 1);
      },
      __wbindgen_object_clone_ref: (i: number) => self_.addHeap(self_.getHeap(i)),
      __wbg_crypto_ed58b8e10a292839: (i: number) => self_.addHeap(self_.getHeap(i).crypto),
      __wbindgen_is_object: (i: number) => {
        const v = self_.getHeap(i);
        return typeof v === "object" && v !== null;
      },
      __wbg_process_5c1d670bc53614b8: (i: number) => self_.addHeap(self_.getHeap(i).process),
      __wbg_versions_c71aa1626a93e0a1: (i: number) => self_.addHeap(self_.getHeap(i).versions),
      __wbg_node_02999533c4ea02e3: (i: number) => self_.addHeap(self_.getHeap(i).node),
      __wbindgen_is_string: (i: number) => typeof self_.getHeap(i) === "string",
      __wbg_require_79b1e9274cde3c87: () =>
        self_.guard(() => self_.addHeap((name: string) => require(name))),
      __wbindgen_is_function: (i: number) => typeof self_.getHeap(i) === "function",
      __wbg_call_7cccdd69e0791ae2: (f: number, t: number, a: number) =>
        self_.guard(() => self_.addHeap(self_.getHeap(f).call(self_.getHeap(t), self_.getHeap(a)))),
      __wbg_msCrypto_0a36e2ec3a343d26: (i: number) => self_.addHeap(self_.getHeap(i).msCrypto),
      __wbg_newwithlength_a381634e90c276d4: (n: number) => self_.addHeap(new Uint8Array(n >>> 0)),
      __wbindgen_is_undefined: (i: number) => self_.getHeap(i) === undefined,
      __wbg_newnoargs_105ed471475aaf50: (p: number, l: number) =>
        self_.addHeap(new Function(self_.str(p, l))),
      __wbg_call_672a4d21634d4a24: (f: number, t: number) =>
        self_.guard(() => self_.addHeap(self_.getHeap(f).call(self_.getHeap(t)))),
      __wbg_static_accessor_GLOBAL_88a902d13a557d07: () => self_.addHeap(globalThis),
      __wbg_static_accessor_GLOBAL_THIS_56578be7e9f832b0: () => self_.addHeap(globalThis),
      __wbg_static_accessor_WINDOW_5de37043a91a9c40: () => 0,
      __wbg_static_accessor_SELF_37c5d418e4bf5819: () => 0,
      __wbindgen_throw: (p: number, l: number) => {
        throw new Error(self_.str(p, l));
      },
      __wbg_randomFillSync_ab2cfe79ebbf2740: (o: number, buf: number) => {
        self_.guard(() => {
          self_.getHeap(o).randomFillSync(self_.takeHeap(buf));
        });
      },
      __wbg_subarray_aa9065fa9dc5df96: (i: number, s: number, e: number) =>
        self_.addHeap(self_.getHeap(i).subarray(s >>> 0, e >>> 0)),
      __wbg_getRandomValues_bcb4912f16000dc4: (o: number, buf: number) => {
        self_.guard(() => {
          self_.getHeap(o).getRandomValues(self_.getHeap(buf));
        });
      },
      __wbg_set_65595bdd868b3009: (dst: number, src: number, off: number) => {
        self_.getHeap(dst).set(self_.getHeap(src), off >>> 0);
      },
    };
    return { wbg } as WebAssembly.Imports;
  }

  /** wasm-bindgen `handleError`: JS exception -> exn_store(export_0) */
  private guard<T>(fn: () => T): T | undefined {
    try {
      return fn();
    } catch (e) {
      this.wasm.__wbindgen_export_0(this.addHeap(e));
      return undefined;
    }
  }

  // ---- result decoding helpers --------------------------------------------
  private withRet<T>(size: number, body: (retptr: number) => void, read: (retptr: number) => T): T {
    const retptr = this.wasm.__wbindgen_add_to_stack_pointer(-16) as number;
    try {
      body(retptr);
      return read(retptr);
    } finally {
      this.wasm.__wbindgen_add_to_stack_pointer(16);
    }
  }

  private getter(name: string, ptr: number): Uint8Array {
    return (this.takeHeap(this.wasm[name](ptr)) as Uint8Array).slice();
  }
  /** sessionId getters return a JS string (44 chars, base64 of 32 bytes), not bytes. */
  private getStr(name: string, ptr: number): string {
    const v = this.takeHeap(this.wasm[name](ptr));
    return typeof v === "string" ? v : new TextDecoder().decode(v);
  }

  // ---- API -----------------------------------------------------------------

  /** Ed25519-style (Curve25519 agreement capable) key pair; used for both IK and EK. */
  generateKeyPair(): KeyPair {
    return this.withRet(
      16,
      (r) => this.wasm.zprotoEd25519GenerateKeyPair(r),
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const err = this.dv().getInt32(r + 4, true);
        if (this.dv().getInt32(r + 8, true)) throw this.takeHeap(err);
        const publicKey = this.getter("zprotoed25519keypair_publicKey", ptr);
        const privateKey = this.getter("zprotoed25519keypair_privateKey", ptr);
        this.wasm.__wbg_zprotoed25519keypair_free(ptr >>> 0, 0);
        return { publicKey, privateKey };
      },
    );
  }

  /** Raw DH "agreement": (ownPrivate 32B, peerPublic 33B) -> 32B shared secret. Order verified offline (swapped order throws InvalidKeySize). */
  calculateAgreement(ownPrivate: Uint8Array, peerPublic: Uint8Array): Uint8Array {
    return this.withRet(
      16,
      (r) => {
        const pa = this.passBytes(ownPrivate), la = this.vecLen;
        const pb = this.passBytes(peerPublic), lb = this.vecLen;
        this.wasm.zprotoEd25519CalculateAgreement(r, pa, la, pb, lb);
      },
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const len = this.dv().getInt32(r + 4, true);
        const err = this.dv().getInt32(r + 8, true);
        if (this.dv().getInt32(r + 12, true)) throw this.takeHeap(err);
        const out = this.u8().slice(ptr >>> 0, (ptr >>> 0) + len);
        this.wasm.__wbindgen_export_1(ptr, len, 1);
        return out;
      },
    );
  }

  /** zprotoSync2GetSessionId(a,b,c) -> string. Inputs unverified (see SPEC.md). */
  getSessionId(a: Uint8Array, b: Uint8Array, c: Uint8Array): string {
    let rp = 0, rl = 0;
    const retptr = this.wasm.__wbindgen_add_to_stack_pointer(-16) as number;
    try {
      const pa = this.passBytes(a), la = this.vecLen;
      const pb = this.passBytes(b), lb = this.vecLen;
      const pc = this.passBytes(c), lc = this.vecLen;
      this.wasm.zprotoSync2GetSessionId(retptr, pa, la, pb, lb, pc, lc);
      const p = this.dv().getInt32(retptr + 0, true);
      const l = this.dv().getInt32(retptr + 4, true);
      const e = this.dv().getInt32(retptr + 8, true);
      const f = this.dv().getInt32(retptr + 12, true);
      rp = p; rl = l;
      if (f) { rp = 0; rl = 0; throw this.takeHeap(e); }
      return this.str(p, l);
    } finally {
      this.wasm.__wbindgen_add_to_stack_pointer(16);
      this.wasm.__wbindgen_export_1(rp, rl, 1);
    }
  }

  /**
   * PHONE SIDE (simulated): create the metadata cipher that carries the first data
   * chunk and establishes the batch session.
   *   plaintext   - the (zstd-compressed, protobuf) first chunk
   *   ownIk       - sender (phone) identity key pair
   *   rxIkPub/rxEkPub - receiver's IK/EK public keys (the `ik` / `ek` base64 fields of the request)
   * Argument roles inferred from the wrapper `createMetaData(e,t,s)` -> h(e, ik.pub, ik.priv, t, s).
   */
  createMetadataCipher(
    plaintext: Uint8Array,
    ownIk: KeyPair,
    rxIkPub: Uint8Array,
    rxEkPub: Uint8Array,
    extra?: string,
  ): MetadataEncrypted {
    return this.withRet(
      16,
      (r) => {
        const a = this.passBytes(plaintext), al = this.vecLen;
        const b = this.passBytes(ownIk.publicKey), bl = this.vecLen;
        const c = this.passBytes(ownIk.privateKey), cl = this.vecLen;
        const d = this.passBytes(rxIkPub), dl = this.vecLen;
        const e = this.passBytes(rxEkPub), el = this.vecLen;
        const f = extra == null ? 0 : this.passStr(extra);
        const fl = this.vecLen;
        this.wasm.zprotoSync2CreateMetadataCipher(r, a, al, b, bl, c, cl, d, dl, e, el, f, fl);
      },
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const err = this.dv().getInt32(r + 4, true);
        if (this.dv().getInt32(r + 8, true)) throw this.takeHeap(err);
        // NB: the original glue reads the *encrypted* wrapper through the
        // zprotosyncmetadatadecrypted_* getters (same layout).
        const out: MetadataEncrypted = {
          sessionId: this.getStr("zprotosyncmetadatadecrypted_sessionId", ptr),
          ciphertext: this.getter("zprotosyncmetadatadecrypted_plaintext", ptr),
          receiverIdentity: this.getter("zprotosyncmetadatadecrypted_senderIdentity", ptr),
          sessionRecord: this.getter("zprotosyncmetadatadecrypted_sessionRecord", ptr),
        };
        this.wasm.__wbg_zprotosyncmetadataencrypted_free(ptr >>> 0, 0);
        return out;
      },
    );
  }

  /** CLIENT SIDE (Web/PC): decrypt a record of a batchType=2 blob. EK private key from the request. */
  decryptMetadata(
    ciphertext: Uint8Array,
    ownIk: KeyPair,
    ekPrivate: Uint8Array,
    extra?: string,
  ): MetadataDecrypted {
    return this.withRet(
      16,
      (r) => {
        const a = this.passBytes(ciphertext), al = this.vecLen;
        const b = this.passBytes(ownIk.publicKey), bl = this.vecLen;
        const c = this.passBytes(ownIk.privateKey), cl = this.vecLen;
        const d = this.passBytes(ekPrivate), dl = this.vecLen;
        const e = extra == null ? 0 : this.passStr(extra);
        const el = this.vecLen;
        this.wasm.zprotoSync2DecryptMetadata(r, a, al, b, bl, c, cl, d, dl, e, el);
      },
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const err = this.dv().getInt32(r + 4, true);
        if (this.dv().getInt32(r + 8, true)) throw this.takeHeap(err);
        const out: MetadataDecrypted = {
          sessionId: this.getStr("zprotosyncmetadatadecrypted_sessionId", ptr),
          plaintext: this.getter("zprotosyncmetadatadecrypted_plaintext", ptr),
          senderIdentity: this.getter("zprotosyncmetadatadecrypted_senderIdentity", ptr),
          sessionRecord: this.getter("zprotosyncmetadatadecrypted_sessionRecord", ptr),
        };
        this.wasm.__wbg_zprotosyncmetadatadecrypted_free(ptr >>> 0, 0);
        return out;
      },
    );
  }

  /** PHONE SIDE (simulated): encrypt a batchType=1 record with the established session record. */
  encryptMessage(plaintext: Uint8Array, sessionRecord: Uint8Array): MessageEncrypted {
    return this.withRet(
      16,
      (r) => {
        const a = this.passBytes(plaintext), al = this.vecLen;
        const b = this.passBytes(sessionRecord), bl = this.vecLen;
        this.wasm.zprotoSync2EncryptMessage(r, a, al, b, bl);
      },
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const err = this.dv().getInt32(r + 4, true);
        if (this.dv().getInt32(r + 8, true)) throw this.takeHeap(err);
        // glue: B (encrypted message) reads through zprotosyncmessagedecrypted_* getters
        const out: MessageEncrypted = {
          sessionId: this.getStr("zprotosyncmessagedecrypted_sessionId", ptr),
          ciphertext: this.getter("zprotosyncmessagedecrypted_plaintext", ptr),
        };
        this.wasm.__wbg_zprotosyncmessageencrypted_free(ptr >>> 0, 0);
        return out;
      },
    );
  }

  /** CLIENT SIDE: decrypt a record of a batchType=1 blob with the sessionRecord from the metadata record. */
  decryptMessage(ciphertext: Uint8Array, sessionRecord: Uint8Array): MessageDecrypted {
    return this.withRet(
      16,
      (r) => {
        const a = this.passBytes(ciphertext), al = this.vecLen;
        const b = this.passBytes(sessionRecord), bl = this.vecLen;
        this.wasm.zprotoSync2DecryptMessage(r, a, al, b, bl);
      },
      (r) => {
        const ptr = this.dv().getInt32(r + 0, true);
        const err = this.dv().getInt32(r + 4, true);
        if (this.dv().getInt32(r + 8, true)) throw this.takeHeap(err);
        const out: MessageDecrypted = {
          sessionId: this.getStr("zprotosyncmessagedecrypted_sessionId", ptr),
          plaintext: this.getter("zprotosyncmessagedecrypted_plaintext", ptr),
        };
        this.wasm.__wbg_zprotosyncmessagedecrypted_free(ptr >>> 0, 0);
        return out;
      },
    );
  }
}
