import {
  BasicPitch,
  noteFramesToTime,
  outputToNotesPoly,
} from "@spotify/basic-pitch";
import * as tf from "@tensorflow/tfjs";

const TARGET_SAMPLE_RATE = 22050;
const FFT_HOP = 256;
const OUTPUT_FRAMES_PER_SECOND = Math.floor(TARGET_SAMPLE_RATE / FFT_HOP);
const AUDIO_WINDOW_SAMPLES = TARGET_SAMPLE_RATE * 2 - FFT_HOP;
const OVERLAP_FRAMES = 30;
const OVERLAP_SAMPLES = OVERLAP_FRAMES * FFT_HOP;
const HALF_OVERLAP_FRAMES = Math.floor(OVERLAP_FRAMES / 2);
const WINDOW_HOP_SAMPLES = AUDIO_WINDOW_SAMPLES - OVERLAP_SAMPLES;
const MAX_AUDIO_SECONDS = 5 * 60;
const MAX_AUDIO_BYTES = 200 * 1024 * 1024;

const MODEL_OUTPUTS = {
  frames: "Identity_1",
  onsets: "Identity_2",
};

// Basic Pitch 1.0.1 leaves the tensors made for each two-second window alive.
// A full song can contain hundreds of windows, so use the same inference logic
// while explicitly releasing every temporary tensor after it has been copied to
// ordinary JavaScript arrays. Contours are intentionally not requested because
// this app does not write pitch-bend events.
class DisposableBasicPitch extends BasicPitch {
  async prepareData(singleChannelAudioData) {
    const framedAudio = tf.tidy(() => {
      const paddedAudio = tf.concat1d([
        tf.zeros([Math.floor(OVERLAP_SAMPLES / 2)], "float32"),
        tf.tensor(singleChannelAudioData),
      ]);
      return tf.expandDims(
        tf.signal.frame(
          paddedAudio,
          AUDIO_WINDOW_SAMPLES,
          WINDOW_HOP_SAMPLES,
          true,
          0,
        ),
        -1,
      );
    });
    return [framedAudio, singleChannelAudioData.length];
  }

  unwrapOutput(result) {
    return tf.tidy(() => {
      const withoutOverlap = result.slice(
        [0, HALF_OVERLAP_FRAMES, 0],
        [-1, result.shape[1] - OVERLAP_FRAMES, -1],
      );
      return withoutOverlap.reshape([
        withoutOverlap.shape[0] * withoutOverlap.shape[1],
        withoutOverlap.shape[2],
      ]);
    });
  }

  async evaluateNotes(resampledBuffer, onChunk, onProgress) {
    if (resampledBuffer.sampleRate !== TARGET_SAMPLE_RATE) {
      throw new Error(`内部重采样失败：${resampledBuffer.sampleRate} Hz`);
    }
    if (resampledBuffer.numberOfChannels !== 1) {
      throw new Error("内部声道转换失败：音频不是单声道。");
    }

    const [framedAudio, originalSampleCount] = await this.prepareData(
      resampledBuffer.getChannelData(0),
    );
    const outputFrameCount = Math.floor(
      originalSampleCount * (OUTPUT_FRAMES_PER_SECOND / TARGET_SAMPLE_RATE),
    );
    const graphModel = await this.model;
    let calculatedFrames = 0;

    try {
      // The WebGL backend lazily compiles its shaders. Without one fully
      // awaited warm-up pass, the first real window can be returned as empty
      // on some WebView2/ANGLE combinations, dropping about 1.6 seconds.
      const warmupInput = tf.slice(framedAudio, 0, 1);
      let warmupResults;
      try {
        warmupResults = graphModel.execute(warmupInput, [
          MODEL_OUTPUTS.frames,
          MODEL_OUTPUTS.onsets,
        ]);
        await Promise.all(warmupResults.map((tensor) => tensor.data()));
      } finally {
        warmupInput.dispose();
        warmupResults?.forEach((tensor) => tensor.dispose());
      }

      for (let index = 0; index < framedAudio.shape[0]; index += 1) {
        onProgress(index / framedAudio.shape[0]);
        const inputWindow = tf.slice(framedAudio, index, 1);
        let frameResult;
        let onsetResult;
        let unwrappedFrames;
        let unwrappedOnsets;

        try {
          const results = graphModel.execute(inputWindow, [
            MODEL_OUTPUTS.frames,
            MODEL_OUTPUTS.onsets,
          ]);
          [frameResult, onsetResult] = results;
          unwrappedFrames = this.unwrapOutput(frameResult);
          unwrappedOnsets = this.unwrapOutput(onsetResult);

          const availableFrames = unwrappedFrames.shape[0];
          if (calculatedFrames < outputFrameCount) {
            const framesToEmit = Math.min(
              availableFrames,
              outputFrameCount - calculatedFrames,
            );
            if (framesToEmit < availableFrames) {
              const trimmedFrames = unwrappedFrames.slice(
                [0, 0],
                [framesToEmit, -1],
              );
              const trimmedOnsets = unwrappedOnsets.slice(
                [0, 0],
                [framesToEmit, -1],
              );
              unwrappedFrames.dispose();
              unwrappedOnsets.dispose();
              unwrappedFrames = trimmedFrames;
              unwrappedOnsets = trimmedOnsets;
            }

            const frameChunk = await unwrappedFrames.array();
            const onsetChunk = await unwrappedOnsets.array();
            onChunk(frameChunk, onsetChunk);
          }
          calculatedFrames += availableFrames;
        } finally {
          inputWindow.dispose();
          frameResult?.dispose();
          onsetResult?.dispose();
          unwrappedFrames?.dispose();
          unwrappedOnsets?.dispose();
        }
      }
      onProgress(1);
    } finally {
      framedAudio.dispose();
    }
  }

  async dispose() {
    const graphModel = await this.model;
    graphModel.dispose();
  }
}

const presets = {
  clean: { onset: 0.35, frame: 0.30, minLength: 8 },
  standard: { onset: 0.25, frame: 0.25, minLength: 5 },
  sensitive: { onset: 0.18, frame: 0.18, minLength: 4 },
};

const state = {
  file: null,
  running: false,
  cancelRequested: false,
};

const elements = {
  fileInput: document.querySelector("#fileInput"),
  dropZone: document.querySelector("#dropZone"),
  fileName: document.querySelector("#fileName"),
  fileMeta: document.querySelector("#fileMeta"),
  title: document.querySelector("#title"),
  mode: document.querySelector("#mode"),
  gameRange: document.querySelector("#gameRange"),
  convert: document.querySelector("#convert"),
  cancel: document.querySelector("#cancel"),
  progress: document.querySelector("#progress"),
  progressText: document.querySelector("#progressText"),
  status: document.querySelector("#status"),
};

function post(message) {
  window.chrome?.webview?.postMessage(message);
}

function setStatus(text, kind = "normal") {
  elements.status.textContent = text;
  elements.status.dataset.kind = kind;
}

function setProgress(percent, text, kind = "normal") {
  const value = Math.max(0, Math.min(100, Math.round(percent)));
  elements.progress.value = value;
  elements.progressText.textContent = `${value}%`;
  if (text) setStatus(text, kind);
}

function humanSize(bytes) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function stem(name) {
  return name.replace(/\.[^.]+$/, "") || "音频扒谱";
}

function selectFile(file) {
  if (!file) return;
  const extension = file.name.split(".").pop()?.toLowerCase();
  if (!["mp3", "wav", "ogg", "flac"].includes(extension)) {
    setStatus("暂不支持这种格式，请改用 MP3、WAV、FLAC 或 OGG。", "error");
    return;
  }
  if (file.size > MAX_AUDIO_BYTES) {
    setStatus("音频超过 200 MB，请先压缩或截取需要扒谱的片段。", "error");
    return;
  }

  state.file = file;
  elements.fileName.textContent = file.name;
  elements.fileMeta.textContent = humanSize(file.size);
  // Setting an input value from script does not apply the HTML maxlength.
  // Keep the generated title within the native host's validated limit so a
  // long but valid Windows file name cannot fail only after full inference.
  elements.title.value = stem(file.name).slice(0, 100);
  elements.dropZone.classList.add("has-file");
  elements.convert.disabled = false;
  setStatus("文件已就绪。建议优先使用人声或单独乐器音频。", "success");
}

async function decodeAndResample(file) {
  if (file.size > MAX_AUDIO_BYTES) {
    throw new Error("音频超过 200 MB，请先压缩或截取需要扒谱的片段。");
  }
  const decodingContext = new AudioContext();
  try {
    const decoded = await decodingContext.decodeAudioData(await file.arrayBuffer());
    if (!Number.isFinite(decoded.duration) || decoded.duration <= 0) {
      throw new Error("音频内容为空或已损坏。");
    }
    if (decoded.duration > MAX_AUDIO_SECONDS) {
      throw new Error("音频超过 5 分钟，请先截取需要扒谱的片段。");
    }

    const frames = Math.max(1, Math.ceil(decoded.duration * TARGET_SAMPLE_RATE));
    const offline = new OfflineAudioContext(1, frames, TARGET_SAMPLE_RATE);
    const source = offline.createBufferSource();
    source.buffer = decoded;
    source.connect(offline.destination);
    source.start(0);
    return await offline.startRendering();
  } finally {
    await decodingContext.close();
  }
}

function midiToFrequency(midi) {
  return 440 * (2 ** ((midi - 69) / 12));
}

async function convert() {
  if (!state.file || state.running) return;
  const title = elements.title.value.trim();
  if (!title) {
    setStatus("请填写曲名。", "error");
    elements.title.focus();
    return;
  }
  if (title.length > 100) {
    setStatus("曲名最多 100 个字符。", "error");
    elements.title.focus();
    return;
  }

  state.running = true;
  state.cancelRequested = false;
  elements.convert.disabled = true;
  elements.cancel.hidden = false;
  elements.cancel.disabled = false;
  elements.fileInput.disabled = true;
  elements.mode.disabled = true;
  elements.gameRange.disabled = true;
  elements.title.disabled = true;

  let model;
  try {
    setProgress(3, "正在读取音频…");
    const audioBuffer = await decodeAndResample(state.file);
    if (state.cancelRequested) throw new Error("__CANCELLED__");

    setProgress(10, "正在加载离线 AI 模型…");
    try {
      if (!(await tf.setBackend("webgl"))) throw new Error("WebGL unavailable");
      await tf.ready();
    } catch {
      if (!(await tf.setBackend("cpu"))) {
        throw new Error("无法启动 AI 计算组件。");
      }
      await tf.ready();
    }
    const modelUrl = new URL("model/model.json", document.baseURI).href;
    model = new DisposableBasicPitch(modelUrl);
    const frames = [];
    const onsets = [];

    await model.evaluateNotes(
      audioBuffer,
      (frameChunk, onsetChunk) => {
        frames.push(...frameChunk);
        onsets.push(...onsetChunk);
      },
      (progress) => {
        if (state.cancelRequested) throw new Error("__CANCELLED__");
        setProgress(10 + progress * 78, "AI 正在识别音高，请稍候…");
      },
    );

    if (state.cancelRequested) throw new Error("__CANCELLED__");
    setProgress(91, "正在整理识别结果…");
    const preset = presets[elements.mode.value] ?? presets.standard;
    const limitRange = elements.gameRange.checked;
    const noteFrames = outputToNotesPoly(
      frames,
      onsets,
      preset.onset,
      preset.frame,
      preset.minLength,
      true,
      limitRange ? midiToFrequency(86) : null,
      limitRange ? midiToFrequency(48) : null,
      true,
      11,
    );
    const notes = noteFramesToTime(noteFrames)
      .filter((note) => !limitRange || (note.pitchMidi >= 48 && note.pitchMidi <= 85))
      .map((note) => ({
        startSeconds: note.startTimeSeconds,
        durationSeconds: note.durationSeconds,
        pitch: note.pitchMidi,
        amplitude: note.amplitude,
      }));

    if (notes.length === 0) {
      throw new Error("没有识别到音符。可以尝试“灵敏”模式，或换用更清晰的音频。");
    }

    setProgress(98, `识别到 ${notes.length} 个音符，正在写入曲库…`);
    elements.cancel.hidden = true;
    elements.cancel.disabled = true;
    post({
      type: "complete",
      title,
      sourceName: state.file.name,
      mode: elements.mode.value,
      limitedToGameRange: limitRange,
      notes,
    });
  } catch (error) {
    if (error?.message === "__CANCELLED__") {
      setProgress(0, "已停止，没有生成文件。");
    } else {
      const message = String(error?.message || error || "未知错误");
      const friendly = /decode|encoding|codec|audio data/i.test(message)
        ? "无法读取这个音频。请转换为 MP3、WAV、FLAC 或 OGG 后重试。"
        : message;
      setStatus(`扒谱失败：${friendly}`, "error");
      post({ type: "error", message: friendly });
    }
    resetRunningState();
  } finally {
    if (model) {
      try {
        await model.dispose();
      } catch {
        // A failed model load can leave only a rejected model promise behind.
      }
    }
  }
}

function resetRunningState() {
  state.running = false;
  elements.convert.disabled = !state.file;
  elements.cancel.hidden = true;
  elements.cancel.disabled = false;
  elements.fileInput.disabled = false;
  elements.mode.disabled = false;
  elements.gameRange.disabled = false;
  elements.title.disabled = false;
}

window.audioToMidiHostSaved = function audioToMidiHostSaved() {
  setProgress(100, "已生成 MIDI 并加入曲库。", "success");
};

window.audioToMidiHostError = function audioToMidiHostError(message) {
  setStatus(`保存失败：${message}`, "error");
  resetRunningState();
};

elements.fileInput.addEventListener("change", () => selectFile(elements.fileInput.files?.[0]));
elements.dropZone.addEventListener("click", () => {
  if (!state.running) elements.fileInput.click();
});
elements.dropZone.addEventListener("keydown", (event) => {
  if (!state.running && (event.key === "Enter" || event.key === " ")) {
    event.preventDefault();
    elements.fileInput.click();
  }
});
for (const eventName of ["dragenter", "dragover"]) {
  elements.dropZone.addEventListener(eventName, (event) => {
    event.preventDefault();
    if (!state.running) elements.dropZone.classList.add("dragging");
  });
}
for (const eventName of ["dragleave", "drop"]) {
  elements.dropZone.addEventListener(eventName, (event) => {
    event.preventDefault();
    elements.dropZone.classList.remove("dragging");
  });
}
elements.dropZone.addEventListener("drop", (event) => {
  if (!state.running) selectFile(event.dataTransfer?.files?.[0]);
});
elements.convert.addEventListener("click", convert);
elements.cancel.addEventListener("click", () => {
  state.cancelRequested = true;
  elements.cancel.disabled = true;
  setStatus("正在停止…");
});

post({ type: "ready" });
