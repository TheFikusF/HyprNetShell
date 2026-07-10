project('hypr_layer', 'c',
  version: '0.1.0',
  default_options: ['c_std=c11', 'warning_level=2'])

cc = meson.get_compiler('c')
wayland_client = dependency('wayland-client')
wayland_egl = dependency('wayland-egl')
egl = dependency('egl')

wayland_scanner = find_program('wayland-scanner')
layer_shell_xml = files('protocols/wlr-layer-shell-unstable-v1.xml')

layer_shell_header = custom_target(
  'wlr-layer-shell-client-header',
  input: layer_shell_xml,
  output: 'wlr-layer-shell-unstable-v1-client-protocol.h',
  command: [wayland_scanner, 'client-header', '@INPUT@', '@OUTPUT@'])

layer_shell_code = custom_target(
  'wlr-layer-shell-private-code',
  input: layer_shell_xml,
  output: 'wlr-layer-shell-unstable-v1-protocol.c',
  command: [wayland_scanner, 'private-code', '@INPUT@', '@OUTPUT@'])

hypr_layer_sources = ['hypr_layer.c', layer_shell_header, layer_shell_code]
hypr_layer_dependencies = [wayland_client, wayland_egl, egl]
hypr_layer_include_directories = include_directories('.')

shared_library(
  'hypr_layer',
  hypr_layer_sources,
  dependencies: hypr_layer_dependencies,
  include_directories: hypr_layer_include_directories,
  install: true)

static_library(
  'hypr_layer',
  hypr_layer_sources,
  dependencies: hypr_layer_dependencies,
  include_directories: hypr_layer_include_directories,
  install: true)
