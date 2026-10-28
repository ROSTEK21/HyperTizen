# HyperTizen

### Color up your Tizen TV with HyperTizen!
HyperTizen is a Hyperion / HyperHDR capturer for Tizen TVs.

<p align="center">
    <a href="https://discord.gg/m2P7v8Y2qR">
       <picture>
           <source height="24px" media="(prefers-color-scheme: dark)" srcset="https://user-images.githubusercontent.com/13122796/178032563-d4e084b7-244e-4358-af50-26bde6dd4996.png" />
           <img height="24px" src="https://user-images.githubusercontent.com/13122796/178032563-d4e084b7-244e-4358-af50-26bde6dd4996.png" />
       </picture>
       </a>
       <a href="https://www.youtube.com/@tizenbrew">
      <picture>
         <source height="24px" media="(prefers-color-scheme: dark)" srcset="https://user-images.githubusercontent.com/13122796/178032714-c51c7492-0666-44ac-99c2-f003a695ab50.png" />
         <img height="24px" src="https://user-images.githubusercontent.com/13122796/178032714-c51c7492-0666-44ac-99c2-f003a695ab50.png" />
     </picture>
     </a>
</p>

# Getting Started

You can read the [guide](./docs/README.md) to get started with HyperTizen.

## Capture

HyperTizen captures the TV picture and sends it to a configured Hyperion or HyperHDR server. When available, the full-frame backend reads the decoded NV12 video plane and averages it into 64 perimeter zones (16 per edge), which are sent to Hyperion as an image.

Protected content can block access to the video plane. After repeated DRM refusals, HyperTizen falls back to the TV's `libvideoenhance` RGB measurement API. A background sampler cycles through 24 spots around the screen, using the slot count and settle time reported by the TV. The latest spot measurements are smoothed and interpolated across the same 64 zones. This fallback reads spot colors, not the protected video frame, and does not decode PQ/HDR data.

The capture libraries and their undocumented APIs vary by TV model, firmware, and signing permissions. The DRM fallback can be less responsive than full-frame capture. On PS5 in Game Mode, flicker was observed on the tested setup; enabling Motion Blur in Game Mode stabilized the output.

## Tested TV

Tested on a Samsung QN95D running Tizen 9.0.

## Acknowledgements

This project is based on [HyperTizen](https://github.com/reisxd/HyperTizen) by Reis Can. video capturing based on [TizenAmbilight](https://github.com/RitwickVerma/TizenAmbilight) by Ritwick Verma and [Tizen OS Ambilight](https://github.com/exzos28/tizen-os-ambilight) by exzos.

A majority of this project's code was written with AI assistance, including GitHub Copilot, and then integrated and tested by the project maintainer.

See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) for third-party license notices.