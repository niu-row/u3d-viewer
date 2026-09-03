cmake_minimum_required(VERSION 3.25)
project(U3DViewerNativeBridge LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "U3DViewer.NativeBridge currently targets Windows/D3D11 only")
endif()

add_library(U3DViewer.NativeBridge SHARED
    src/NativeBridge.cpp
    src/ScenePresenter.cpp
)

target_compile_features(U3DViewer.NativeBridge PRIVATE cxx_std_20)
target_compile_definitions(U3DViewer.NativeBridge PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_link_libraries(U3DViewer.NativeBridge PRIVATE d3d11 d3dcompiler dxgi user32)

set_target_properties(U3DViewer.NativeBridge PROPERTIES
    OUTPUT_NAME "U3DViewer.NativeBridge"
)
