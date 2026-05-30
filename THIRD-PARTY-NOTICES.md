# Third-party notices

This project links against and builds source from the following third-party component.

## basis_universal

- Project: https://github.com/BinomialLLC/basis_universal
- License: Apache License 2.0
- Pinned commit: `e4f439fc9545b6a9e1fd26fc7ffd0c682c4b96d4`

The native library is compiled from this project's transcoder module
(`transcoder/basisu_transcoder.cpp` and its headers) together with the shim in `native/src`.
The block encoder entry points used here — `basist::bc7f::fast_pack_bc7_auto_rgba`,
`basist::encode_bc1`, `basist::encode_bc4`, and `basist::astc_6x6_hdr::fast_encode_bc6h` — are part
of basis_universal.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these files except
in compliance with the License. You may obtain a copy of the License at:

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is
distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
implied. See the License for the specific language governing permissions and limitations under the
License.

The full upstream license text is available in the basis_universal repository at the pinned commit
above (its `LICENSE` file).
