project('hypr_layer', 'c',
  version: '0.1.0',
  default_options: ['c_std=c11', 'warning_level=2'])

cc = meson.get_compiler('c')
wayland_client = dependency('wayland-client')
wayland_egl = dependency('wayland-egl')
egl = dependency('egl')
xkbcommon = dependency('xkbcommon')
pam = dependency('pam')
wireplumber = dependency('wireplumber-0.5', version: '>= 0.5.0', required: false)
threads = dependency('threads')
libm = cc.find_library('m', required: true)

wayland_scanner = find_program('wayland-scanner')
layer_shell_xml = files('protocols/wlr-layer-shell-unstable-v1.xml')
session_lock_xml = files('protocols/ext-session-lock-v1.xml')
image_capture_source_xml = files('protocols/ext-image-capture-source-v1.xml')
image_copy_capture_xml = files('protocols/ext-image-copy-capture-v1.xml')
data_control_xml = files('protocols/ext-data-control-v1.xml')
foreign_toplevel_xml = files('protocols/ext-foreign-toplevel-list-v1.xml')

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

session_lock_header = custom_target(
  'ext-session-lock-client-header',
  input: session_lock_xml,
  output: 'ext-session-lock-v1-client-protocol.h',
  command: [wayland_scanner, 'client-header', '@INPUT@', '@OUTPUT@'])

session_lock_code = custom_target(
  'ext-session-lock-private-code',
  input: session_lock_xml,
  output: 'ext-session-lock-v1-protocol.c',
  command: [wayland_scanner, 'private-code', '@INPUT@', '@OUTPUT@'])

capture_protocols = []
foreach protocol_definition : [
  ['ext-foreign-toplevel-list-v1', foreign_toplevel_xml],
  ['ext-image-capture-source-v1', image_capture_source_xml],
  ['ext-image-copy-capture-v1', image_copy_capture_xml],
  ['ext-data-control-v1', data_control_xml],
]
  protocol_name = protocol_definition[0]
  protocol = protocol_definition[1]
  capture_protocols += custom_target(
    protocol_name + '-client-header',
    input: protocol,
    output: protocol_name + '-client-protocol.h',
    command: [wayland_scanner, 'client-header', '@INPUT@', '@OUTPUT@'])
  capture_protocols += custom_target(
    protocol_name + '-private-code',
    input: protocol,
    output: protocol_name + '-protocol.c',
    command: [wayland_scanner, 'private-code', '@INPUT@', '@OUTPUT@'])
endforeach

hypr_layer_sources = [
  'hypr_layer.c',
  'hypr_lock.c',
  layer_shell_header,
  layer_shell_code,
  session_lock_header,
  session_lock_code,
  capture_protocols,
]
hypr_layer_dependencies = [wayland_client, wayland_egl, egl, xkbcommon, pam, threads]
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
  install: false)

if wireplumber.found()
  shared_library(
    'hypr_audio',
    ['hypr_audio.c'],
    dependencies: [wireplumber, threads, libm],
    include_directories: hypr_layer_include_directories,
    install: false)
endif
